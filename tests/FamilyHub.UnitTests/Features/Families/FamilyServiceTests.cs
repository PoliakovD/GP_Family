using FamilyHub.Api.Features.Families;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Families;

public class FamilyServiceTests : SqliteTestBase
{
    private readonly FamilyService _sut;

    public FamilyServiceTests()
    {
        _sut = new FamilyService(
            Db, new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance), NullLogger<FamilyService>.Instance);
    }

    [Fact]
    public async Task CreateFamilyAsync_MakesCreatorActiveAdmin()
    {
        var creator = Db.AddUser();

        var (result, familyId) = await _sut.CreateFamilyAsync(creator.Id, "My Family");

        result.Should().Be(CreateFamilyResult.Success);
        var member = Db.FamilyMembers.Single(m => m.FamilyId == familyId && m.UserId == creator.Id);
        member.Role.Should().Be(FamilyRole.Admin);
        member.Status.Should().Be(MemberStatus.Active);
    }

    [Fact]
    public async Task CreateFamilyAsync_AtLimit_ReturnsLimitExceeded_AndDoesNotCreateFamily()
    {
        var creator = Db.AddUser();
        for (var i = 0; i < FamilyService.MaxFamiliesPerUser; i++)
        {
            var (result, _) = await _sut.CreateFamilyAsync(creator.Id, $"Family {i}");
            result.Should().Be(CreateFamilyResult.Success);
        }

        var (limitResult, familyId) = await _sut.CreateFamilyAsync(creator.Id, "One too many");

        limitResult.Should().Be(CreateFamilyResult.LimitExceeded);
        familyId.Should().Be(Guid.Empty);
        Db.FamilyMembers.Count(m => m.UserId == creator.Id && m.Role == FamilyRole.Admin)
            .Should().Be(FamilyService.MaxFamiliesPerUser);
    }

    [Fact]
    public async Task GetMyFamiliesAsync_IncludesPendingApprovalMemberships()
    {
        var user = Db.AddUser();
        var (family, _) = Db.SeedFamilyWithAdmin();
        Db.FamilyMembers.Add(TestData.NewMember(family.Id, user.Id, FamilyRole.Member, MemberStatus.PendingApproval));
        await Db.SaveChangesAsync();

        var result = await _sut.GetMyFamiliesAsync(user.Id);

        result.Should().ContainSingle(f => f.Id == family.Id && f.MyStatus == MemberStatus.PendingApproval);
    }

    [Fact]
    public async Task GetMyFamiliesAsync_OnlyOwnMemberships()
    {
        var (familyA, adminA) = Db.SeedFamilyWithAdmin("A");
        Db.SeedFamilyWithAdmin("B");

        var result = await _sut.GetMyFamiliesAsync(adminA.Id);

        result.Should().ContainSingle().Which.Id.Should().Be(familyA.Id);
    }
}
