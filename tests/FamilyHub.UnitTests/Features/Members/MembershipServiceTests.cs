using FamilyHub.Api.Features.Members;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Members;

public class MembershipServiceTests : SqliteTestBase
{
    private readonly MembershipService _sut;

    public MembershipServiceTests()
    {
        _sut = new MembershipService(Db, new FamilyAccessService(Db));
    }

    [Fact]
    public async Task RemoveMemberAsync_NonAdmin_ReturnsForbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var requester = Db.AddMember(family.Id, FamilyRole.Member);
        var target = Db.AddMember(family.Id, FamilyRole.Member);

        var result = await _sut.RemoveMemberAsync(family.Id, target.Id, requester.Id);

        result.Should().Be(RemoveMemberResult.Forbidden);
    }

    [Fact]
    public async Task RemoveMemberAsync_UnknownTarget_ReturnsNotFound()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var result = await _sut.RemoveMemberAsync(family.Id, Guid.NewGuid(), admin.Id);

        result.Should().Be(RemoveMemberResult.NotFound);
    }

    [Fact]
    public async Task RemoveMemberAsync_LastActiveAdmin_IsRejected()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        // Сам-на-себя: единственный активный админ пытается удалить себя же.
        var result = await _sut.RemoveMemberAsync(family.Id, admin.Id, admin.Id);

        result.Should().Be(RemoveMemberResult.LastAdmin);
        Db.FamilyMembers.Any(m => m.FamilyId == family.Id && m.UserId == admin.Id).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveMemberAsync_NotLastAdmin_Removes()
    {
        var (family, admin1) = Db.SeedFamilyWithAdmin();
        var admin2 = Db.AddMember(family.Id, FamilyRole.Admin);

        var result = await _sut.RemoveMemberAsync(family.Id, admin1.Id, admin2.Id);

        result.Should().Be(RemoveMemberResult.Removed);
        Db.FamilyMembers.Any(m => m.FamilyId == family.Id && m.UserId == admin1.Id).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveMemberAsync_RemovesMemberMedicalSharesForThatFamily()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id, FamilyRole.Member);
        Db.FamilyMedicalShares.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = member.Id,
            FamilyId = family.Id,
            SharedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.RemoveMemberAsync(family.Id, member.Id, admin.Id);

        Db.FamilyMedicalShares.Any(s => s.OwnerUserId == member.Id && s.FamilyId == family.Id).Should().BeFalse();
    }

    [Fact]
    public async Task LeaveFamilyAsync_DoesNotRequireAdminRole()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id, FamilyRole.Member);

        var result = await _sut.LeaveFamilyAsync(family.Id, member.Id);

        result.Should().Be(LeaveFamilyResult.Left);
    }

    [Fact]
    public async Task LeaveFamilyAsync_LastActiveAdmin_IsRejected()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var result = await _sut.LeaveFamilyAsync(family.Id, admin.Id);

        result.Should().Be(LeaveFamilyResult.LastAdmin);
    }

    [Fact]
    public async Task LeaveFamilyAsync_UnknownMembership_ReturnsNotFound()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();

        var result = await _sut.LeaveFamilyAsync(family.Id, Guid.NewGuid());

        result.Should().Be(LeaveFamilyResult.NotFound);
    }
}
