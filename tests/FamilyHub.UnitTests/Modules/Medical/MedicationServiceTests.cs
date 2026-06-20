using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicationServiceTests : SqliteTestBase
{
    private readonly MedicationService _sut;

    public MedicationServiceTests()
    {
        _sut = new MedicationService(Db, new FamilyAccessService(Db));
    }

    [Fact]
    public async Task GetForFamilyAsync_Member_SeesFamilyMedications()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        Db.Medications.Add(TestData.NewMedication(family.Id, admin.Id));
        await Db.SaveChangesAsync();

        var (result, items) = await _sut.GetForFamilyAsync(family.Id, admin.Id);

        result.Should().Be(MedicationAccessResult.Success);
        items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetForFamilyAsync_NonMemberOfFamily_Forbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var outsider = Db.AddUser();

        var (result, items) = await _sut.GetForFamilyAsync(family.Id, outsider.Id);

        result.Should().Be(MedicationAccessResult.Forbidden);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForFamilyAsync_PendingApprovalMember_Forbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var pending = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);

        var (result, _) = await _sut.GetForFamilyAsync(family.Id, pending.Id);

        result.Should().Be(MedicationAccessResult.Forbidden);
    }

    [Fact]
    public async Task CreateAsync_Member_CanAdd()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var (result, item) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateMedicationRequest("Aspirin", null, null, 10));

        result.Should().Be(MedicationAccessResult.Success);
        item!.Name.Should().Be("Aspirin");
    }

    [Fact]
    public async Task UpdateAsync_LoadsByIdThenChecksRealFamilyId_OtherFamilyMember_Forbidden()
    {
        var (familyA, adminA) = Db.SeedFamilyWithAdmin("A");
        var medication = TestData.NewMedication(familyA.Id, adminA.Id);
        Db.Medications.Add(medication);
        await Db.SaveChangesAsync();

        var (familyB, adminB) = Db.SeedFamilyWithAdmin("B");

        var result = await _sut.UpdateAsync(
            medication.Id, adminB.Id, new UpdateMedicationRequest("Renamed", null, null, 5));

        result.Should().Be(MedicationAccessResult.Forbidden);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_NotFound()
    {
        var result = await _sut.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateMedicationRequest("X", null, null, 1));

        result.Should().Be(MedicationAccessResult.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_Member_Removes()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medication = TestData.NewMedication(family.Id, admin.Id);
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
        var medication = TestData.NewMedication(family.Id, admin.Id);
        Db.Medications.Add(medication);
        await Db.SaveChangesAsync();
        var outsider = Db.AddUser();

        var result = await _sut.DeleteAsync(medication.Id, outsider.Id);

        result.Should().Be(MedicationAccessResult.Forbidden);
        Db.Medications.Any(m => m.Id == medication.Id).Should().BeTrue();
    }
}
