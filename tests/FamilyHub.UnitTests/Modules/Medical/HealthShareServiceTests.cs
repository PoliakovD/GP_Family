using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Modules.Medical.Access;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

/// <summary>«Кто видит моё здоровье» (ADR-0017) — управление грантами доступа per-категория.</summary>
public class HealthShareServiceTests : SqliteTestBase
{
    private readonly HealthShareService _sut;

    public HealthShareServiceTests()
    {
        var family = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        _sut = new HealthShareService(Db, family, new CourseSubjects(Db), NullLogger<HealthShareService>.Instance);
    }

    [Fact]
    public async Task GetMine_ListsEveryActiveFamilyMember_DefaultingToNone()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var a = Db.AddMember(family.Id);
        var b = Db.AddMember(family.Id);
        a.FirstName = "Аня";
        b.FirstName = "Боря";
        Db.SaveChanges();

        var mine = await _sut.GetMineAsync(admin.Id);

        mine.Should().HaveCount(2);
        mine.Should().OnlyContain(g => g.Categories == HealthShareCategory.None);
        mine.Select(g => g.Name).Should().Equal("Аня", "Боря"); // по имени
    }

    [Fact]
    public async Task SetAsync_ForFamilyMember_StoresCategories_AndZeroRemovesTheRow()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var viewer = Db.AddMember(family.Id);

        (await _sut.SetAsync(admin.Id, viewer.Id, HealthShareCategory.Diary | HealthShareCategory.Vaccinations))
            .Should().Be(HealthShareResult.Success);
        Db.HealthShareGrants.AsNoTracking().Single().Categories.Should().Be(HealthShareCategory.Diary | HealthShareCategory.Vaccinations);

        (await _sut.SetAsync(admin.Id, viewer.Id, HealthShareCategory.None)).Should().Be(HealthShareResult.Success);
        Db.HealthShareGrants.Should().BeEmpty();
    }

    [Fact]
    public async Task SetAsync_ForNonMemberOrSelf_IsInvalid()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var stranger = Db.AddUser();

        (await _sut.SetAsync(admin.Id, stranger.Id, HealthShareCategory.Diary)).Should().Be(HealthShareResult.Invalid);
        (await _sut.SetAsync(admin.Id, admin.Id, HealthShareCategory.Diary)).Should().Be(HealthShareResult.Invalid);
        Db.HealthShareGrants.Should().BeEmpty();
    }

    [Fact]
    public async Task GrantCategoryAsync_AddsBit_WithoutClearingOthers()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var viewer = Db.AddMember(family.Id);
        await _sut.SetAsync(admin.Id, viewer.Id, HealthShareCategory.Diary);

        await _sut.GrantCategoryAsync(admin.Id, viewer.Id, HealthShareCategory.Intake);

        Db.HealthShareGrants.AsNoTracking().Single().Categories
            .Should().Be(HealthShareCategory.Diary | HealthShareCategory.Intake);
    }

    [Fact]
    public async Task GetSharedWithMe_OnlyWhileCommonActiveFamily()
    {
        var (family, owner) = Db.SeedFamilyWithAdmin();
        var viewer = Db.AddMember(family.Id);
        await _sut.SetAsync(owner.Id, viewer.Id, HealthShareCategory.Diary);

        var shared = await _sut.GetSharedWithMeAsync(viewer.Id);
        shared.Should().ContainSingle(s => s.OwnerUserId == owner.Id && s.Categories == HealthShareCategory.Diary);

        Db.FamilyMembers.Remove(Db.FamilyMembers.Single(m => m.FamilyId == family.Id && m.UserId == viewer.Id));
        Db.SaveChanges();

        (await _sut.GetSharedWithMeAsync(viewer.Id)).Should().BeEmpty();
        Db.HealthShareGrants.Should().ContainSingle(); // строка остаётся, просто больше не действует
    }
}
