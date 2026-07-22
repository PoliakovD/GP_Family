using FamilyHub.Api.Features.Invites;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Invites;

public class InviteServiceTests : SqliteTestBase
{
    private readonly InviteService _sut;

    public InviteServiceTests()
    {
        _sut = new InviteService(
            Db, new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance),
            new OutboxTestPipeline(Db).Writer, NullLogger<InviteService>.Instance);
    }

    [Fact]
    public async Task CreateInviteAsync_NonAdmin_ReturnsForbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id, FamilyRole.Member);

        var (result, invite) = await _sut.CreateInviteAsync(
            member.Id, family.Id, new CreateInviteRequest(null, FamilyRole.Member, 1, null));

        result.Should().Be(CreateInviteResult.Forbidden);
        invite.Should().BeNull();
    }

    [Fact]
    public async Task CreateInviteAsync_PersonalInvite_AlwaysMaxUsesOne()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var targetUserId = Guid.NewGuid();

        var (result, invite) = await _sut.CreateInviteAsync(
            admin.Id, family.Id, new CreateInviteRequest(targetUserId, FamilyRole.Member, 50, null));

        result.Should().Be(CreateInviteResult.Created);
        invite!.MaxUses.Should().Be(1);
        invite.TargetUserId.Should().Be(targetUserId);
    }

    [Fact]
    public async Task CreateInviteAsync_LinkInvite_UsesRequestedMaxUses()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var (_, invite) = await _sut.CreateInviteAsync(
            admin.Id, family.Id, new CreateInviteRequest(null, FamilyRole.Member, 5, null));

        invite!.MaxUses.Should().Be(5);
        invite.TargetUserId.Should().BeNull();
    }

    [Fact]
    public async Task RedeemInviteAsync_UnknownCode_ReturnsNotFound()
    {
        var result = await _sut.RedeemInviteAsync("does-not-exist", Guid.NewGuid());

        result.Should().Be(RedeemResult.NotFound);
    }

    [Fact]
    public async Task RedeemInviteAsync_RevokedInvite_ReturnsRevoked()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var invite = TestData.NewInvite(family.Id, admin.Id, isRevoked: true);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, Guid.NewGuid());

        result.Should().Be(RedeemResult.Revoked);
    }

    [Fact]
    public async Task RedeemInviteAsync_ExpiredInvite_ReturnsExpired()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var invite = TestData.NewInvite(family.Id, admin.Id, expiresAt: DateTime.UtcNow.AddDays(-1));
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, Guid.NewGuid());

        result.Should().Be(RedeemResult.Expired);
    }

    [Fact]
    public async Task RedeemInviteAsync_ExhaustedInvite_ReturnsExhausted()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 1);
        invite.UsedCount = 1;
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, Guid.NewGuid());

        result.Should().Be(RedeemResult.Exhausted);
    }

    [Fact]
    public async Task RedeemInviteAsync_PersonalInviteForSomeoneElse_ReturnsNotForYou()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var invite = TestData.NewInvite(family.Id, admin.Id, targetUserId: Guid.NewGuid());
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, Guid.NewGuid());

        result.Should().Be(RedeemResult.NotForYou);
    }

    [Fact]
    public async Task RedeemInviteAsync_AlreadyMember_ReturnsAlreadyMember()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var existingMember = Db.AddMember(family.Id);
        var invite = TestData.NewInvite(family.Id, admin.Id);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, existingMember.Id);

        result.Should().Be(RedeemResult.AlreadyMember);
    }

    [Fact]
    public async Task RedeemInviteAsync_PersonalInvite_JoinsActiveImmediately()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var targetUser = Db.AddUser();
        var invite = TestData.NewInvite(family.Id, admin.Id, targetUserId: targetUser.Id);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, targetUser.Id);

        result.Should().Be(RedeemResult.Joined);
        var member = Db.FamilyMembers.Single(m => m.FamilyId == family.Id && m.UserId == targetUser.Id);
        member.Status.Should().Be(MemberStatus.Active);
        Db.FamilyInvites.Single(i => i.Id == invite.Id).UsedCount.Should().Be(1);
    }

    [Fact]
    public async Task RedeemInviteAsync_LinkInvite_ResultsInPendingApproval()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var user = Db.AddUser();
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 5);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RedeemInviteAsync(invite.Code, user.Id);

        result.Should().Be(RedeemResult.PendingApproval);
        var member = Db.FamilyMembers.Single(m => m.FamilyId == family.Id && m.UserId == user.Id);
        member.Status.Should().Be(MemberStatus.PendingApproval);
    }

    [Fact]
    public async Task RedeemInviteAsync_TwiceBySameUser_SecondRedemptionIsRejectedByAlreadyMember()
    {
        // Защита от повторного редимита того же юзера: после первого успешного редимита
        // FamilyMember уже существует => вторая попытка ловится проверкой AlreadyMember,
        // а не UNIQUE-индексом на FamilyInviteRedemption (тот же эффект, другой путь).
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var user = Db.AddUser();
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 5);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var first = await _sut.RedeemInviteAsync(invite.Code, user.Id);
        var second = await _sut.RedeemInviteAsync(invite.Code, user.Id);

        first.Should().Be(RedeemResult.PendingApproval);
        second.Should().Be(RedeemResult.AlreadyMember);
        Db.FamilyInviteRedemptions.Count(r => r.FamilyInviteId == invite.Id && r.UserId == user.Id).Should().Be(1);
    }

    [Fact]
    public async Task RevokeInviteAsync_NonAdmin_ReturnsForbidden()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id, FamilyRole.Member);
        var invite = TestData.NewInvite(family.Id, admin.Id);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RevokeInviteAsync(invite.Id, member.Id);

        result.Should().Be(RevokeInviteResult.Forbidden);
    }

    [Fact]
    public async Task RevokeInviteAsync_UnknownInvite_ReturnsNotFound()
    {
        var result = await _sut.RevokeInviteAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().Be(RevokeInviteResult.NotFound);
    }

    [Fact]
    public async Task RevokeInviteAsync_Admin_MarksRevoked()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var invite = TestData.NewInvite(family.Id, admin.Id);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();

        var result = await _sut.RevokeInviteAsync(invite.Id, admin.Id);

        result.Should().Be(RevokeInviteResult.Revoked);
        Db.FamilyInvites.Single(i => i.Id == invite.Id).IsRevoked.Should().BeTrue();
    }

    [Fact]
    public async Task GetPendingMembersAsync_NonAdmin_ReturnsForbidden()
    {
        var (family, _) = Db.SeedFamilyWithAdmin();
        var member = Db.AddMember(family.Id, FamilyRole.Member);

        var (result, pending) = await _sut.GetPendingMembersAsync(family.Id, member.Id);

        result.Should().Be(ApproveRejectResult.Forbidden);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveMemberAsync_Admin_SetsActive()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var pendingUser = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);

        var result = await _sut.ApproveMemberAsync(family.Id, pendingUser.Id, admin.Id);

        result.Should().Be(ApproveRejectResult.Success);
        Db.FamilyMembers.Single(m => m.FamilyId == family.Id && m.UserId == pendingUser.Id)
            .Status.Should().Be(MemberStatus.Active);
    }

    [Fact]
    public async Task ApproveMemberAsync_NoPendingMember_ReturnsNotFound()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();

        var result = await _sut.ApproveMemberAsync(family.Id, Guid.NewGuid(), admin.Id);

        result.Should().Be(ApproveRejectResult.NotFound);
    }

    [Fact]
    public async Task RejectMemberAsync_RemovesMembership_ButKeepsUsedCountUnchanged()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var user = Db.AddUser();
        var invite = TestData.NewInvite(family.Id, admin.Id, maxUses: 5);
        Db.FamilyInvites.Add(invite);
        await Db.SaveChangesAsync();
        await _sut.RedeemInviteAsync(invite.Code, user.Id);

        var usedCountBefore = Db.FamilyInvites.Single(i => i.Id == invite.Id).UsedCount;

        var result = await _sut.RejectMemberAsync(family.Id, user.Id, admin.Id);

        result.Should().Be(ApproveRejectResult.Success);
        Db.FamilyMembers.Any(m => m.FamilyId == family.Id && m.UserId == user.Id).Should().BeFalse();
        Db.FamilyInvites.Single(i => i.Id == invite.Id).UsedCount.Should().Be(usedCountBefore);
    }
}
