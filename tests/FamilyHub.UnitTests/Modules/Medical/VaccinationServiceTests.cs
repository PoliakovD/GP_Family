using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.Access;
using FamilyHub.Modules.Medical.Vaccinations;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class VaccinationServiceTests : SqliteTestBase
{
    private readonly VaccinationService _sut;
    private readonly VaccinationAccess _access;

    public VaccinationServiceTests()
    {
        var family = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        var scope = new SubjectScopeService(Db, family);
        _access = new VaccinationAccess(Db, scope);
        var subjects = new VaccinationSubjects(Db);
        _sut = new VaccinationService(Db, _access, subjects, Substitute.For<IFileStorage>(), NullLogger<VaccinationService>.Instance);
    }

    private static CreateVaccinationRequest MmrDose(string kind, Guid id, DateOnly date) =>
        new(kind, id, "mmr", 0, null, null, VaccinationKind.Done, date, VaccinationDatePrecision.Day, null, false);

    [Fact]
    public async Task CreateAsync_ForSelf_SavesAndReflectsInSchedule()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = new DateOnly(2020, 1, 1);
        await Db.SaveChangesAsync();

        var (result, item, error) = await _sut.CreateAsync(admin.Id, MmrDose(VaccinationSubjects.UserKind, admin.Id, new DateOnly(2021, 1, 1)));

        result.Should().Be(VaccinationResult.Success);
        item!.Status.Should().Be(VaccinationStatus.Done);

        var (scheduleResult, schedule) = await _sut.GetPersonScheduleAsync(admin.Id, VaccinationSubjects.UserKind, admin.Id);
        scheduleResult.Should().Be(VaccinationResult.Success);
        schedule!.ByAge.Should().Contain(i => i.SeriesCode == "mmr" && i.DoseIndex == 0 && i.Status == VaccinationStatus.Done);
    }

    [Fact]
    public async Task CreateAsync_ForDependent_ByOutsider_ReturnsNotFound()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var dependent = AddDependent(family.Id, admin.Id);
        var outsider = Db.AddUser();

        var (result, _, _) = await _sut.CreateAsync(outsider.Id, MmrDose(VaccinationSubjects.DependentKind, dependent.Id, new DateOnly(2021, 1, 1)));

        result.Should().Be(VaccinationResult.NotFound);
    }

    [Fact]
    public async Task CreateAsync_ByFamilyMember_ForDependent_Succeeds()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var dependent = AddDependent(family.Id, admin.Id, birth: new DateOnly(2020, 1, 1));
        var member = Db.AddMember(family.Id);

        var (result, item, _) = await _sut.CreateAsync(member.Id, MmrDose(VaccinationSubjects.DependentKind, dependent.Id, new DateOnly(2021, 1, 1)));

        result.Should().Be(VaccinationResult.Success);
        item!.Status.Should().Be(VaccinationStatus.Done);
    }

    [Fact]
    public async Task Watcher_OfAdult_CanReadButNotCreate()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = new DateOnly(1990, 1, 1);
        var watcher = Db.AddMember(family.Id);
        Grant(admin.Id, watcher.Id, HealthShareCategory.Vaccinations);

        var (createResult, _, _) = await _sut.CreateAsync(watcher.Id, MmrDose(VaccinationSubjects.UserKind, admin.Id, new DateOnly(2021, 1, 1)));
        createResult.Should().Be(VaccinationResult.Forbidden);

        var (readResult, schedule) = await _sut.GetPersonScheduleAsync(watcher.Id, VaccinationSubjects.UserKind, admin.Id);
        readResult.Should().Be(VaccinationResult.Success);
        schedule.Should().NotBeNull();
    }

    [Fact]
    public async Task Watcher_WithMedicationWatcherRowButNoGrant_HasNoAccess()
    {
        // ADR-0017: MedicationWatcher больше не даёт доступ сам по себе — только уведомления.
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = new DateOnly(1990, 1, 1);
        var watcher = Db.AddMember(family.Id);
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = admin.Id, WatcherUserId = watcher.Id, ReceiveReminders = true, CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var (readResult, schedule) = await _sut.GetPersonScheduleAsync(watcher.Id, VaccinationSubjects.UserKind, admin.Id);
        readResult.Should().Be(VaccinationResult.NotFound);
        schedule.Should().BeNull();
    }

    [Fact]
    public async Task Watcher_WithOnlyIntakeGrant_HasNoVaccinationAccess()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = new DateOnly(1990, 1, 1);
        var watcher = Db.AddMember(family.Id);
        Grant(admin.Id, watcher.Id, HealthShareCategory.Intake);

        var (readResult, _) = await _sut.GetPersonScheduleAsync(watcher.Id, VaccinationSubjects.UserKind, admin.Id);
        readResult.Should().Be(VaccinationResult.NotFound);
    }

    [Fact]
    public async Task Watcher_WhoLeftFamily_LosesAccess_DespiteGrantStillStored()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = new DateOnly(1990, 1, 1);
        var watcher = Db.AddMember(family.Id);
        Grant(admin.Id, watcher.Id, HealthShareCategory.Vaccinations);

        Db.FamilyMembers.Remove(Db.FamilyMembers.Single(m => m.FamilyId == family.Id && m.UserId == watcher.Id));
        await Db.SaveChangesAsync();

        var (readResult, _) = await _sut.GetPersonScheduleAsync(watcher.Id, VaccinationSubjects.UserKind, admin.Id);
        readResult.Should().Be(VaccinationResult.NotFound);
    }

    private void Grant(Guid ownerId, Guid viewerId, HealthShareCategory categories)
    {
        Db.HealthShareGrants.Add(new HealthShareGrant
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId, ViewerUserId = viewerId,
            Categories = categories, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();
    }

    [Fact]
    public async Task BulkMarkAsync_MixedKinds_SavesAll()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var dependent = AddDependent(family.Id, admin.Id, birth: new DateOnly(2019, 1, 1));

        var items = new List<BulkMarkItem>
        {
            new("bcg", 0, VaccinationKind.Done, new DateOnly(2019, 1, 5), VaccinationDatePrecision.Day),
            new("bcg", 1, VaccinationKind.Unknown, null, null),
            new("mmr", 0, VaccinationKind.HadDisease, new DateOnly(2020, 6, 1), VaccinationDatePrecision.Month),
        };

        var (result, saved, _) = await _sut.BulkMarkAsync(admin.Id, new BulkMarkRequest(VaccinationSubjects.DependentKind, dependent.Id, items, null));

        result.Should().Be(VaccinationResult.Success);
        saved!.Saved.Should().Be(3);

        var (_, schedule) = await _sut.GetPersonScheduleAsync(admin.Id, VaccinationSubjects.DependentKind, dependent.Id);
        schedule!.ByAge.Should().Contain(i => i.SeriesCode == "bcg" && i.DoseIndex == 0 && i.Status == VaccinationStatus.Done);
        schedule.ByAge.Should().Contain(i => i.SeriesCode == "bcg" && i.DoseIndex == 1 && i.Status == VaccinationStatus.NoData);
        // "mmr" закрыт болезнью целиком — один пункт со статусом HadDisease.
        schedule.ByAge.Where(i => i.SeriesCode == "mmr").Should().ContainSingle(i => i.Status == VaccinationStatus.HadDisease);
    }

    [Fact]
    public async Task DeleteAsync_ByOwner_Succeeds_AndByOutsider_IsNotFound()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = new DateOnly(1990, 1, 1);
        await Db.SaveChangesAsync();
        var (_, item, _) = await _sut.CreateAsync(admin.Id, MmrDose(VaccinationSubjects.UserKind, admin.Id, new DateOnly(2021, 1, 1)));

        var outsider = Db.AddUser();
        (await _sut.DeleteAsync(outsider.Id, item!.RecordId!.Value)).Should().Be(VaccinationResult.NotFound);

        (await _sut.DeleteAsync(admin.Id, item.RecordId!.Value)).Should().Be(VaccinationResult.Success);
        Db.Vaccinations.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOverviewAsync_PersonWithoutBirthDate_GetsHintAndNoAttention()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var dependent = AddDependent(family.Id, admin.Id, birth: null);

        var overview = await _sut.GetOverviewAsync(admin.Id);

        var summary = overview.People.Single(p => p.Subject.Id == dependent.Id);
        summary.Hint.Should().NotBeNull();
        overview.Attention.Should().NotContain(a => a.Subject.Id == dependent.Id);
    }

    [Fact]
    public async Task GetOverviewAsync_DueSoonDose_AppearsInAttention()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        // Ребёнку только что исполнилось 6 лет — ревакцинация MMR уже в окне (DueSoon).
        var dependent = AddDependent(family.Id, admin.Id, birth: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-6));

        var overview = await _sut.GetOverviewAsync(admin.Id);

        overview.Attention.Should().Contain(a => a.Subject.Id == dependent.Id && a.Item.SeriesCode == "mmr");
    }

    private FamilyDependent AddDependent(Guid familyId, Guid createdBy, DateOnly? birth = null, string name = "Ребёнок")
    {
        var d = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = familyId, FirstName = name, Gender = Gender.Female,
            BirthDate = birth, CreatedByUserId = createdBy, CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(d);
        Db.SaveChanges();
        return d;
    }
}
