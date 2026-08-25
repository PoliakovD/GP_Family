using FamilyHub.Api.Features.Account;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Account;

/// <summary>
/// Слияние Telegram-only аккаунта (source) в веб/email-аккаунт (target) при привязке
/// Telegram с подтверждением от бота. Выживает всегда target; source удаляется.
/// </summary>
public class AccountMergeServiceTests : SqliteTestBase
{
    private readonly AccountMergeService _sut;

    public AccountMergeServiceTests()
    {
        _sut = new AccountMergeService(Db, NullLogger<AccountMergeService>.Instance);
    }

    private User AddTargetUser(string? username = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            Username = username,
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    private User AddSourceTelegramUser(long telegramId, string? tgUsername = null, string? appUsername = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            TelegramId = telegramId,
            TgUsername = tgUsername,
            Username = appUsername,
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task MergeAsync_MovesTelegramIdAndTgUsernameToTarget_AndDeletesSource()
    {
        var target = AddTargetUser();
        var source = AddSourceTelegramUser(555, tgUsername: "source_handle");

        await _sut.MergeAsync(source.Id, target.Id);

        Db.Users.Should().NotContain(u => u.Id == source.Id);
        var merged = Db.Users.Single(u => u.Id == target.Id);
        merged.TelegramId.Should().Be(555);
        merged.TgUsername.Should().Be("source_handle");
    }

    [Fact]
    public async Task MergeAsync_TargetHasNoUsername_AdoptsSourceUsername()
    {
        var target = AddTargetUser(username: null);
        var source = AddSourceTelegramUser(556, appUsername: "cool_handle");

        await _sut.MergeAsync(source.Id, target.Id);

        Db.Users.Single(u => u.Id == target.Id).Username.Should().Be("cool_handle");
    }

    [Fact]
    public async Task MergeAsync_TargetAlreadyHasUsername_KeepsTargetUsername()
    {
        var target = AddTargetUser(username: "target_handle");
        var source = AddSourceTelegramUser(557, appUsername: "source_handle");

        await _sut.MergeAsync(source.Id, target.Id);

        Db.Users.Single(u => u.Id == target.Id).Username.Should().Be("target_handle");
    }

    [Fact]
    public async Task MergeAsync_SourceOnlyFamilyMembership_IsReassignedToTarget()
    {
        var target = AddTargetUser();
        var source = AddSourceTelegramUser(558);
        var family = TestData.NewFamily();
        Db.Families.Add(family);
        Db.FamilyMembers.Add(TestData.NewMember(family.Id, source.Id, FamilyRole.Member, MemberStatus.Active));
        await Db.SaveChangesAsync();

        await _sut.MergeAsync(source.Id, target.Id);

        Db.FamilyMembers.Should().ContainSingle(m => m.FamilyId == family.Id && m.UserId == target.Id);
    }

    [Fact]
    public async Task MergeAsync_BothMembersOfSameFamily_KeepsHigherRoleAndStatus_RemovesSourceRow()
    {
        var target = AddTargetUser();
        var source = AddSourceTelegramUser(559);
        var family = TestData.NewFamily();
        Db.Families.Add(family);
        // target — обычный member, source — админ той же семьи (например, создал её через бота).
        Db.FamilyMembers.Add(TestData.NewMember(family.Id, target.Id, FamilyRole.Member, MemberStatus.Active));
        Db.FamilyMembers.Add(TestData.NewMember(family.Id, source.Id, FamilyRole.Admin, MemberStatus.Active));
        await Db.SaveChangesAsync();

        await _sut.MergeAsync(source.Id, target.Id);

        var memberships = Db.FamilyMembers.Where(m => m.FamilyId == family.Id).ToList();
        memberships.Should().ContainSingle();
        memberships.Single().UserId.Should().Be(target.Id);
        memberships.Single().Role.Should().Be(FamilyRole.Admin, "source был админом — привилегия не должна теряться при слиянии");
    }

    [Fact]
    public async Task MergeAsync_MedicalRecordsAndMedkitsAndInvites_AreReassignedToTarget()
    {
        var target = AddTargetUser();
        var source = AddSourceTelegramUser(560);
        var family = TestData.NewFamily();
        Db.Families.Add(family);
        var medkit = TestData.NewMedkit(family.Id, source.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, source.Id));
        Db.MedicalRecords.Add(TestData.NewMedicalRecord(source.Id));
        Db.FamilyInvites.Add(TestData.NewInvite(family.Id, source.Id));
        await Db.SaveChangesAsync();

        await _sut.MergeAsync(source.Id, target.Id);

        // MergeAsync переносит эти сущности через ExecuteUpdateAsync (bulk SQL), который
        // намеренно обходит change tracker — сущности, уже отслеживаемые ЭТИМ контекстом
        // (Add() выше), останутся в памяти со старыми значениями. Читаем через свежий
        // контекст на той же БД, как и другие тесты гонок в этой кодовой базе (см.
        // UserProvisioningServiceTests.GetOrCreateUserIdAsync_RaceOnInsert_RereadsInsteadOfThrowing).
        using var verify = NewContext();
        verify.Medkits.Single().CreatedByUserId.Should().Be(target.Id);
        verify.Medications.Single().CreatedByUserId.Should().Be(target.Id);
        verify.MedicalRecords.Single().OwnerUserId.Should().Be(target.Id);
        verify.FamilyInvites.Single().CreatedByUserId.Should().Be(target.Id);
    }

    [Fact]
    public async Task MergeAsync_UserConsents_AreLeftUntouched_AsLegalRecords()
    {
        var target = AddTargetUser();
        var source = AddSourceTelegramUser(561);
        Db.Set<UserConsent>().Add(new UserConsent
        {
            Id = Guid.NewGuid(),
            UserId = source.Id,
            Kind = ConsentKind.PdnConsent,
            Version = "v1",
            AcceptedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await _sut.MergeAsync(source.Id, target.Id);

        // FK-less юридическая запись переживает удаление source — UserId остаётся историческим.
        Db.Set<UserConsent>().Single().UserId.Should().Be(source.Id);
    }
}
