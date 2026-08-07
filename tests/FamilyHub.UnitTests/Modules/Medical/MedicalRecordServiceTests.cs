using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicalRecordServiceTests : SqliteTestBase
{
    private readonly MedicalRecordService _sut;

    public MedicalRecordServiceTests()
    {
        _sut = new MedicalRecordService(
            Db, new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance),
            new TestSupport.OutboxTestPipeline(Db).Writer,
            new FamilyHub.Infrastructure.Audit.MedicalAuditWriter(Db),
            new RussianTextSearcher(),
            Substitute.For<IFileStorage>(),
            NullLogger<MedicalRecordService>.Instance);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_Owner_AlwaysSeesOwnRecord()
    {
        var owner = Db.AddUser();
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id));
        await Db.SaveChangesAsync();

        var result = await _sut.GetVisibleRecordsAsync(owner.Id);

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_NotSharedYet_OtherFamilyMemberDoesNotSeeIt()
    {
        var owner = Db.AddUser();
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        var (family, _) = Db.SeedFamilyWithAdmin();
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var result = await _sut.GetVisibleRecordsAsync(familyMate.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_SharedAndActiveMember_Sees()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var shareResult = await _sut.ShareWithFamilyAsync(owner.Id, family.Id);

        shareResult.Should().Be(MedicalRecordAccessResult.Success);
        var result = await _sut.GetVisibleRecordsAsync(familyMate.Id);
        result.Should().ContainSingle(r => r.Id == record.Id);
    }

    [Fact]
    public async Task ShareWithFamilyAsync_OwnerNotMemberOfThatFamily_Forbidden()
    {
        var owner = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();

        var result = await _sut.ShareWithFamilyAsync(owner.Id, family.Id);

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_PendingApprovalMember_DoesNotSeeSharedRecord()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        var pending = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);
        await Db.SaveChangesAsync();

        var result = await _sut.GetVisibleRecordsAsync(pending.Id);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_HiddenFromSpecificFamily_NotVisibleToThatFamilyButVisibleToOwner()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var hideResult = await _sut.HideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        hideResult.Should().Be(MedicalRecordAccessResult.Success);
        (await _sut.GetVisibleRecordsAsync(familyMate.Id)).Should().BeEmpty();
        (await _sut.GetVisibleRecordsAsync(owner.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task HideFromFamiliesAsync_NotOwner_ForbiddenEvenForFamilyAdmin()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();

        var result = await _sut.HideFromFamiliesAsync(admin.Id, record.Id, [family.Id]);

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
    }

    [Fact]
    public async Task UnhideFromFamiliesAsync_RestoresVisibility()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        var familyMate = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();
        await _sut.HideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        var result = await _sut.UnhideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        result.Should().Be(MedicalRecordAccessResult.Success);
        (await _sut.GetVisibleRecordsAsync(familyMate.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task UnshareFamilyAsync_DoesNotClearExistingHiddenMarkers()
    {
        // Инвариант 5: Unshare не чистит MedicalRecordHidden — повторный Share вернёт то же скрытие.
        var (family, _) = Db.SeedFamilyWithAdmin();
        var owner = Db.AddMember(family.Id);
        var record = TestData.NewMedicalRecord(owner.Id);
        Db.MedicalRecords.Add(record);
        await Db.SaveChangesAsync();
        await _sut.ShareWithFamilyAsync(owner.Id, family.Id);
        await _sut.HideFromFamiliesAsync(owner.Id, record.Id, [family.Id]);

        var unshareResult = await _sut.UnshareFamilyAsync(owner.Id, family.Id);
        unshareResult.Should().Be(MedicalRecordAccessResult.Success);
        var reshareResult = await _sut.ShareWithFamilyAsync(owner.Id, family.Id);
        reshareResult.Should().Be(MedicalRecordAccessResult.Success);

        var familyMate = Db.AddMember(family.Id);
        (await _sut.GetVisibleRecordsAsync(familyMate.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_HideFromFamilyIds_AppliesTripleIntersection()
    {
        // Запрошено скрыть от семьи, в которой владелец не состоит активным членом — не должно попасть.
        var (myFamily, _) = Db.SeedFamilyWithAdmin("Mine");
        var owner = Db.AddMember(myFamily.Id);
        var (otherFamily, _) = Db.SeedFamilyWithAdmin("Other");

        Db.FamilyMedicalShares.Add(new FamilyMedicalShare
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.Id,
            FamilyId = myFamily.Id,
            SharedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var (_, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Self", new DateOnly(2024, 1, 1), null, null, [myFamily.Id, otherFamily.Id]));

        var hidden = Db.MedicalRecordHiddens.Where(h => h.MedicalRecordId == dto!.Id).Select(h => h.FamilyId).ToList();
        hidden.Should().ContainSingle().Which.Should().Be(myFamily.Id);
    }

    [Fact]
    public async Task GetVisibleRecordsAsync_FilterByKind_OnlyReturnsMatchingKind()
    {
        var owner = Db.AddUser();
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.Analysis));
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit));
        await Db.SaveChangesAsync();

        var analyses = await _sut.GetVisibleRecordsAsync(owner.Id, MedicalRecordKind.Analysis);
        var visits = await _sut.GetVisibleRecordsAsync(owner.Id, MedicalRecordKind.DoctorVisit);
        var all = await _sut.GetVisibleRecordsAsync(owner.Id);

        analyses.Should().ContainSingle(r => r.Kind == MedicalRecordKind.Analysis);
        visits.Should().ContainSingle(r => r.Kind == MedicalRecordKind.DoctorVisit);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_FilterByKind_DoesNotMatchOtherKind()
    {
        // types=visit не должен находить (и, следовательно, расшифровывать) анализы, и наоборот —
        // ключевая гарантия для SearchService.SearchMedicalRecordsAsync.
        var owner = Db.AddUser();
        var analysis = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.Analysis);
        analysis.PersonName = "Иванов";
        var visit = TestData.NewMedicalRecord(owner.Id, MedicalRecordKind.DoctorVisit);
        visit.PersonName = "Иванов";
        Db.MedicalRecords.AddRange(analysis, visit);
        await Db.SaveChangesAsync();

        var visitHits = await _sut.SearchAsync(owner.Id, "Иванов", MedicalRecordKind.DoctorVisit);
        var analysisHits = await _sut.SearchAsync(owner.Id, "Иванов", MedicalRecordKind.Analysis);
        var allHits = await _sut.SearchAsync(owner.Id, "Иванов");

        visitHits.Should().ContainSingle(h => h.Record.Id == visit.Id);
        analysisHits.Should().ContainSingle(h => h.Record.Id == analysis.Id);
        allHits.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateAsync_SetsKindFromRequest()
    {
        var owner = Db.AddUser();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Пациент", new DateOnly(2024, 1, 1), "Доктор", null, null, MedicalRecordKind.DoctorVisit));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.Kind.Should().Be(MedicalRecordKind.DoctorVisit);
        (await Db.MedicalRecords.FindAsync(dto.Id))!.Kind.Should().Be(MedicalRecordKind.DoctorVisit);
    }

    [Fact]
    public async Task CreateAsync_BothDependentAndTargetSet_ReturnsInvalidTarget()
    {
        var owner = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();
        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = family.Id, Name = "Барсик", IsPet = true, CreatedByUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(dependent);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Пациент", new DateOnly(2024, 1, 1), null, null, null,
            FamilyDependentId: dependent.Id, TargetUserId: Guid.NewGuid()));

        result.Should().Be(MedicalRecordAccessResult.InvalidTarget);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForDependent_MemberOfDependentFamily_Succeeds_AndVisibleToOtherActiveMember()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var otherMember = Db.AddMember(family.Id);
        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = family.Id, Name = "Барсик", IsPet = true, CreatedByUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(dependent);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Барсик", new DateOnly(2024, 1, 1), "Ветеринар", null, null, FamilyDependentId: dependent.Id));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.OwnerUserId.Should().Be(owner.Id, "владелец — тот, кто физически загрузил, а не подопечный");
        (await _sut.GetVisibleRecordsAsync(otherMember.Id)).Should().ContainSingle(r => r.Id == dto.Id);
    }

    [Fact]
    public async Task CreateAsync_ForDependentOfAnotherFamily_ReturnsForbidden()
    {
        var owner = Db.AddUser();
        var (otherFamily, otherAdmin) = Db.SeedFamilyWithAdmin();
        var dependent = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = otherFamily.Id, Name = "Чужой", IsPet = false, CreatedByUserId = otherAdmin.Id,
            CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(dependent);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Чужой", new DateOnly(2024, 1, 1), null, null, null, FamilyDependentId: dependent.Id));

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForTargetUserWithoutSharedFamily_ReturnsForbidden()
    {
        var owner = Db.AddUser();
        var stranger = Db.AddUser();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Пациент", new DateOnly(2024, 1, 1), null, null, null, TargetUserId: stranger.Id));

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
        dto.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ForTargetUserInSameFamily_Succeeds_AndVisibleToTarget_ButOwnerStaysUploader()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var target = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();

        var (result, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Пациент", new DateOnly(2024, 1, 1), null, null, null, TargetUserId: target.Id));

        result.Should().Be(MedicalRecordAccessResult.Success);
        dto!.OwnerUserId.Should().Be(owner.Id);
        dto.TargetUserId.Should().Be(target.Id);
        (await _sut.GetVisibleRecordsAsync(target.Id)).Should().ContainSingle(r => r.Id == dto.Id);
    }

    [Fact]
    public async Task DeleteAsync_Owner_Succeeds_AndRemovesRecord()
    {
        var owner = Db.AddUser();
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(owner.Id));
        await Db.SaveChangesAsync();
        var record = await Db.MedicalRecords.FirstAsync(r => r.OwnerUserId == owner.Id);

        var result = await _sut.DeleteAsync(owner.Id, record.Id);

        result.Should().Be(MedicalRecordAccessResult.Success);
        // ExecuteDeleteAsync — bulk-операция в обход change tracker'а: FindAsync вернул бы
        // устаревший закэшированный экземпляр (record уже отслеживается тем же Db-контекстом
        // после чтения выше) вместо реального состояния БД — нужен AsNoTracking, чтобы форсировать
        // настоящий запрос.
        (await Db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == record.Id)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_TargetUser_CannotDelete_UnconditionalOwnerOnlyRule()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var target = Db.AddMember(family.Id);
        await Db.SaveChangesAsync();
        var (_, dto) = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Пациент", new DateOnly(2024, 1, 1), null, null, null, TargetUserId: target.Id));

        var result = await _sut.DeleteAsync(target.Id, dto!.Id);

        result.Should().Be(MedicalRecordAccessResult.Forbidden);
        (await Db.MedicalRecords.FindAsync(dto.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownRecord_ReturnsNotFound()
    {
        var owner = Db.AddUser();

        var result = await _sut.DeleteAsync(owner.Id, Guid.NewGuid());

        result.Should().Be(MedicalRecordAccessResult.NotFound);
    }
}
