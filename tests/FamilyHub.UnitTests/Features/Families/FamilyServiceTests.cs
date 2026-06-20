using FamilyHub.Api.Features.Families;
using FamilyHub.Domain.Enums;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Families;

public class FamilyServiceTests : SqliteTestBase
{
    private readonly FamilyService _sut;

    public FamilyServiceTests()
    {
        _sut = new FamilyService(Db);
    }

    [Fact]
    public async Task CreateFamilyAsync_MakesCreatorActiveAdmin()
    {
        var creator = Db.AddUser();

        var familyId = await _sut.CreateFamilyAsync(creator.Id, "My Family");

        var member = Db.FamilyMembers.Single(m => m.FamilyId == familyId && m.UserId == creator.Id);
        member.Role.Should().Be(FamilyRole.Admin);
        member.Status.Should().Be(MemberStatus.Active);
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
