using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Modules.Medical.Medkits;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedkitServiceTests : SqliteTestBase
{
    private readonly MedkitService _sut;

    public MedkitServiceTests()
    {
        _sut = new MedkitService(Db, new FamilyAccessService(Db));
    }

    [Fact]
    public async Task GetForFamilyAsync_Member_SeesFamilyMedkits()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        Db.Medkits.Add(TestData.NewMedkit(family.Id, admin.Id));
        await Db.SaveChangesAsync();

        var (result, items) = await _sut.GetForFamilyAsync(family.Id, admin.Id);

        result.Should().Be(MedkitAccessResult.Success);
        items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetForFamilyAsync_NonMemberOfFamily_Forbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var outsider = Db.AddUser();

        var (result, items) = await _sut.GetForFamilyAsync(family.Id, outsider.Id);

        result.Should().Be(MedkitAccessResult.Forbidden);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_Member_CanAdd_AndFamilyCanHaveSeveralMedkits()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var (result1, item1) = await _sut.CreateAsync(family.Id, admin.Id, new CreateMedkitRequest("Домашняя аптечка"));
        var (result2, item2) = await _sut.CreateAsync(family.Id, admin.Id, new CreateMedkitRequest("Дорожная аптечка"));

        result1.Should().Be(MedkitAccessResult.Success);
        result2.Should().Be(MedkitAccessResult.Success);
        item1!.Id.Should().NotBe(item2!.Id);

        var (_, items) = await _sut.GetForFamilyAsync(family.Id, admin.Id);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateAsync_LoadsByIdThenChecksRealFamilyId_OtherFamilyMember_Forbidden()
    {
        var (familyA, adminA) = Db.SeedFamilyWithAdmin("A");
        var medkit = TestData.NewMedkit(familyA.Id, adminA.Id);
        Db.Medkits.Add(medkit);
        await Db.SaveChangesAsync();

        var (familyB, adminB) = Db.SeedFamilyWithAdmin("B");

        var result = await _sut.UpdateAsync(medkit.Id, adminB.Id, new UpdateMedkitRequest("Renamed"));

        result.Should().Be(MedkitAccessResult.Forbidden);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_NotFound()
    {
        var result = await _sut.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateMedkitRequest("X"));

        result.Should().Be(MedkitAccessResult.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_Member_Removes()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        await Db.SaveChangesAsync();

        var result = await _sut.DeleteAsync(medkit.Id, admin.Id);

        result.Should().Be(MedkitAccessResult.Success);
        Db.Medkits.Any(k => k.Id == medkit.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_CascadesToMedications()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, admin.Id));
        await Db.SaveChangesAsync();

        await _sut.DeleteAsync(medkit.Id, admin.Id);

        Db.Medications.Any(m => m.MedkitId == medkit.Id).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_OutsiderOfFamily_Forbidden()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        await Db.SaveChangesAsync();
        var outsider = Db.AddUser();

        var result = await _sut.DeleteAsync(medkit.Id, outsider.Id);

        result.Should().Be(MedkitAccessResult.Forbidden);
        Db.Medkits.Any(k => k.Id == medkit.Id).Should().BeTrue();
    }
}
