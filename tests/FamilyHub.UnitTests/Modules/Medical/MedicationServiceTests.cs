using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicationServiceTests : SqliteTestBase
{
    private readonly MedicationService _sut;

    public MedicationServiceTests()
    {
        // IEnrichmentRequestService — заглушка: реализация ходит raw SQL к Postgres-специфичным
        // функциям (см. KbLookupService), не исполняется против SQLite этого теста.
        _sut = new MedicationService(
            Db,
            new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance),
            Substitute.For<IEnrichmentRequestService>(),
            NullLogger<MedicationService>.Instance);
    }

    [Fact]
    public async Task GetForMedkitAsync_Member_SeesMedkitMedications()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, admin.Id));
        await Db.SaveChangesAsync();

        var (result, items) = await _sut.GetForMedkitAsync(medkit.Id, admin.Id);

        result.Should().Be(MedicationAccessResult.Success);
        items.Should().ContainSingle();
    }

    /// <summary>§5 плана «живой конвейер» — GetForMedkitAsync теперь джойнит на
    /// MedicationEnrichmentJobs (Pending/Running, точное совпадение NormalizedName, см. class doc
    /// MedicationDto.EnrichmentPending) и должно выставить чип "уточняем…", когда обогащение
    /// этого медикамента ещё не завершилось.</summary>
    [Fact]
    public async Task GetForMedkitAsync_MatchingPendingEnrichmentJob_MarksEnrichmentPending()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        var medication = TestData.NewMedication(medkit.Id, family.Id, admin.Id);
        Db.Medications.Add(medication);
        Db.MedicationEnrichmentJobs.Add(new MedicationEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = MedicationNameNormalizer.Normalize(medication.Name),
            SourceDisplayName = medication.Name,
            MedicationId = medication.Id,
            RequestedByUserId = admin.Id,
            FamilyId = family.Id,
            Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var (result, items) = await _sut.GetForMedkitAsync(medkit.Id, admin.Id);

        result.Should().Be(MedicationAccessResult.Success);
        items.Should().ContainSingle().Which.EnrichmentPending.Should().BeTrue();
    }

    /// <summary>Зеркало теста выше — задача уже Completed, значит обогащение завершилось и чип
    /// показывать не за что (не только "нет джобы вовсе", но и "джоба была и закрылась").</summary>
    [Fact]
    public async Task GetForMedkitAsync_CompletedEnrichmentJob_EnrichmentPendingFalse()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        var medication = TestData.NewMedication(medkit.Id, family.Id, admin.Id);
        Db.Medications.Add(medication);
        Db.MedicationEnrichmentJobs.Add(new MedicationEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = MedicationNameNormalizer.Normalize(medication.Name),
            SourceDisplayName = medication.Name,
            MedicationId = medication.Id,
            RequestedByUserId = admin.Id,
            FamilyId = family.Id,
            Status = EnrichmentJobStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var (_, items) = await _sut.GetForMedkitAsync(medkit.Id, admin.Id);

        items.Should().ContainSingle().Which.EnrichmentPending.Should().BeFalse();
    }

    [Fact]
    public async Task GetForMedkitAsync_NonMemberOfFamily_Forbidden()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        await Db.SaveChangesAsync();
        var outsider = Db.AddUser();

        var (result, items) = await _sut.GetForMedkitAsync(medkit.Id, outsider.Id);

        result.Should().Be(MedicationAccessResult.Forbidden);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForMedkitAsync_PendingApprovalMember_Forbidden()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        await Db.SaveChangesAsync();
        var pending = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);

        var (result, _) = await _sut.GetForMedkitAsync(medkit.Id, pending.Id);

        result.Should().Be(MedicationAccessResult.Forbidden);
    }

    [Fact]
    public async Task GetForMedkitAsync_UnknownMedkitId_NotFound()
    {
        var (result, items) = await _sut.GetForMedkitAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().Be(MedicationAccessResult.NotFound);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_Member_CanAdd()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        await Db.SaveChangesAsync();

        var (result, item) = await _sut.CreateAsync(
            medkit.Id, admin.Id, new CreateMedicationRequest("Aspirin", null, new Dictionary<string, string> { ["quantity"] = "10" }));

        result.Should().Be(MedicationAccessResult.Success);
        item!.Name.Should().Be("Aspirin");
        item.MedkitId.Should().Be(medkit.Id);
        item.FamilyId.Should().Be(family.Id);
    }

    [Fact]
    public async Task CreateAsync_UnknownMedkitId_NotFound()
    {
        var (result, item) = await _sut.CreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), new CreateMedicationRequest("Aspirin", null, null));

        result.Should().Be(MedicationAccessResult.NotFound);
        item.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_LoadsByIdThenChecksRealFamilyId_OtherFamilyMember_Forbidden()
    {
        var (familyA, adminA) = Db.SeedFamilyWithAdmin("A");
        var medkitA = TestData.NewMedkit(familyA.Id, adminA.Id);
        Db.Medkits.Add(medkitA);
        var medication = TestData.NewMedication(medkitA.Id, familyA.Id, adminA.Id);
        Db.Medications.Add(medication);
        await Db.SaveChangesAsync();

        var (familyB, adminB) = Db.SeedFamilyWithAdmin("B");

        var result = await _sut.UpdateAsync(
            medication.Id, adminB.Id, new UpdateMedicationRequest("Renamed", null, null));

        result.Should().Be(MedicationAccessResult.Forbidden);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_NotFound()
    {
        var result = await _sut.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateMedicationRequest("X", null, null));

        result.Should().Be(MedicationAccessResult.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_Member_Removes()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        var medication = TestData.NewMedication(medkit.Id, family.Id, admin.Id);
        Db.Medications.Add(medication);
        await Db.SaveChangesAsync();

        var result = await _sut.DeleteAsync(medication.Id, admin.Id);

        result.Should().Be(MedicationAccessResult.Success);
        Db.Medications.Any(m => m.Id == medication.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OutsiderOfFamily_Forbidden()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        var medication = TestData.NewMedication(medkit.Id, family.Id, admin.Id);
        Db.Medications.Add(medication);
        await Db.SaveChangesAsync();
        var outsider = Db.AddUser();

        var result = await _sut.DeleteAsync(medication.Id, outsider.Id);

        result.Should().Be(MedicationAccessResult.Forbidden);
        Db.Medications.Any(m => m.Id == medication.Id).Should().BeTrue();
    }
}
