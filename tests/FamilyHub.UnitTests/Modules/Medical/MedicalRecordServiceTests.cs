using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

        var dto = await _sut.CreateAsync(owner.Id, new CreateMedicalRecordRequest(
            "Self", new DateOnly(2024, 1, 1), null, null, [myFamily.Id, otherFamily.Id]));

        var hidden = Db.MedicalRecordHiddens.Where(h => h.MedicalRecordId == dto.Id).Select(h => h.FamilyId).ToList();
        hidden.Should().ContainSingle().Which.Should().Be(myFamily.Id);
    }
}
