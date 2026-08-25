using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Modules.Birthdays.Birthdays;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Birthdays;

public class BirthdayServiceTests : SqliteTestBase
{
    private readonly BirthdayService _sut;

    public BirthdayServiceTests()
    {
        _sut = new BirthdayService(
            Db, new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance), NullLogger<BirthdayService>.Instance);
    }

    [Fact]
    public async Task GetForFamilyAsync_Member_SeesFamilyBirthdays()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        Db.Birthdays.Add(TestData.NewBirthday(family.Id));
        await Db.SaveChangesAsync();

        var (result, items) = await _sut.GetForFamilyAsync(family.Id, admin.Id);

        result.Should().Be(BirthdayAccessResult.Success);
        // Ручная запись — единственная в Birthdays; TestData.NewUser() (см. SeedFamilyWithAdmin)
        // задаёт BirthDate по умолчанию, поэтому сам admin тоже попадает в список отдельной
        // Member-строкой (identity rework, см. GetForFamilyAsync_UnionsManualMemberAndDependentSources).
        items.Should().ContainSingle(b => b.Source == BirthdaySource.Manual);
    }

    [Fact]
    public async Task GetForFamilyAsync_UnionsManualMemberAndDependentSources()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin(); // admin.BirthDate задан TestData.NewUser() по умолчанию
        Db.Birthdays.Add(TestData.NewBirthday(family.Id));
        Db.FamilyDependents.Add(new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = family.Id, FirstName = "Барсик", IsPet = true,
            Gender = Gender.Male, BirthDate = new DateOnly(2019, 6, 1), CreatedByUserId = admin.Id, CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var (result, items) = await _sut.GetForFamilyAsync(family.Id, admin.Id);

        result.Should().Be(BirthdayAccessResult.Success);
        items.Should().HaveCount(3);
        items.Should().ContainSingle(b => b.Source == BirthdaySource.Manual);
        items.Should().ContainSingle(b => b.Source == BirthdaySource.Member && b.Id == admin.Id);
        items.Should().ContainSingle(b => b.Source == BirthdaySource.Dependent && b.PersonName == "Барсик");
    }

    [Fact]
    public async Task GetForFamilyAsync_PendingMember_BirthDateNotIncluded()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);
        await Db.SaveChangesAsync();

        var (_, items) = await _sut.GetForFamilyAsync(family.Id, admin.Id);

        // Только сам admin (Active) — PendingApproval не даёт доступа ни к чему, в т.ч. не должен
        // фигурировать в списке дней рождения семьи (тот же инвариант, что и для остальных
        // семейных ресурсов).
        items.Should().ContainSingle(b => b.Source == BirthdaySource.Member);
    }

    [Fact]
    public async Task GetForFamilyAsync_OutsiderOfFamily_Forbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var outsider = Db.AddUser();

        var (result, _) = await _sut.GetForFamilyAsync(family.Id, outsider.Id);

        result.Should().Be(BirthdayAccessResult.Forbidden);
    }

    [Fact]
    public async Task CreateAsync_Member_CanAdd()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var (result, item) = await _sut.CreateAsync(
            family.Id, admin.Id, new CreateBirthdayRequest("Mom", new DateOnly(1996, 2, 29)));

        result.Should().Be(BirthdayAccessResult.Success);
        item!.PersonName.Should().Be("Mom");
    }

    [Fact]
    public async Task UpdateAsync_LoadsByIdThenChecksRealFamilyId_OtherFamilyMember_Forbidden()
    {
        var (familyA, _) = Db.SeedFamilyWithAdmin("A");
        var birthday = TestData.NewBirthday(familyA.Id);
        Db.Birthdays.Add(birthday);
        await Db.SaveChangesAsync();

        var (_, adminB) = Db.SeedFamilyWithAdmin("B");

        var result = await _sut.UpdateAsync(birthday.Id, adminB.Id, new UpdateBirthdayRequest("Renamed", birthday.Date));

        result.Should().Be(BirthdayAccessResult.Forbidden);
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_NotFound()
    {
        var result = await _sut.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateBirthdayRequest("X", new DateOnly(2000, 1, 1)));

        result.Should().Be(BirthdayAccessResult.NotFound);
    }

    [Fact]
    public async Task DeleteAsync_Member_Removes()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var birthday = TestData.NewBirthday(family.Id);
        Db.Birthdays.Add(birthday);
        await Db.SaveChangesAsync();

        var result = await _sut.DeleteAsync(birthday.Id, admin.Id);

        result.Should().Be(BirthdayAccessResult.Success);
        Db.Birthdays.Any(b => b.Id == birthday.Id).Should().BeFalse();
    }
}
