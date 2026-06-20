using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Authorization;

public class FamilyAccessServiceTests : SqliteTestBase
{
    private readonly FamilyAccessService _sut;

    public FamilyAccessServiceTests()
    {
        _sut = new FamilyAccessService(Db);
    }

    [Fact]
    public async Task HasRoleAsync_ActiveMemberAtOrAboveMinRole_ReturnsTrue()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var result = await _sut.HasRoleAsync(admin.Id, family.Id, FamilyRole.Member);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasRoleAsync_PendingApproval_ReturnsFalse()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var pending = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);

        var result = await _sut.HasRoleAsync(pending.Id, family.Id, FamilyRole.Member);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasRoleAsync_RoleBelowMinRole_ReturnsFalse()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id, FamilyRole.Member);

        var result = await _sut.HasRoleAsync(member.Id, family.Id, FamilyRole.Admin);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasRoleAsync_NotAMember_ReturnsFalse()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();

        var result = await _sut.HasRoleAsync(Guid.NewGuid(), family.Id, FamilyRole.Member);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveFamilyIdsAsync_ExcludesPendingApproval()
    {
        var user = Db.AddUser();
        var (activeFamily, _) = Db.SeedFamilyWithAdmin("Active");
        Db.FamilyMembers.Add(TestData.NewMember(activeFamily.Id, user.Id, FamilyRole.Member, MemberStatus.Active));
        var (pendingFamily, _) = Db.SeedFamilyWithAdmin("Pending");
        Db.FamilyMembers.Add(TestData.NewMember(pendingFamily.Id, user.Id, FamilyRole.Member, MemberStatus.PendingApproval));
        await Db.SaveChangesAsync();

        var result = await _sut.GetActiveFamilyIdsAsync(user.Id);

        result.Should().ContainSingle().Which.Should().Be(activeFamily.Id);
    }
}
