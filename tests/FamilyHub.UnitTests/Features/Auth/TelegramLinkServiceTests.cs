using FamilyHub.Api.Features.Account;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Domain.Entities;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Auth;

/// <summary>
/// Привязка Telegram к веб/email-аккаунту "с подтверждением с другой стороны": код
/// генерируется в настройках, подтверждается ConfirmAsync (эмулирует нажатие кнопки ботом).
/// </summary>
public class TelegramLinkServiceTests : SqliteTestBase
{
    private readonly TelegramLinkService _sut;

    public TelegramLinkServiceTests()
    {
        var merge = new AccountMergeService(Db, NullLogger<AccountMergeService>.Instance);
        _sut = new TelegramLinkService(Db, merge, NullLogger<TelegramLinkService>.Instance);
    }

    private User AddWebUser(string? email = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email ?? $"{Guid.NewGuid():N}@example.com",
            PinHash = "hash",
            DisplayName = "Web User",
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task StartAsync_UserWithoutTelegram_IssuesCode()
    {
        var user = AddWebUser();

        var (result, code, expiresAt) = await _sut.StartAsync(user.Id);

        result.Should().Be(StartLinkTelegramResult.Started);
        code.Should().NotBeNullOrEmpty();
        expiresAt.Should().BeAfter(DateTime.UtcNow);
        Db.TelegramLinkCodes.Should().ContainSingle(c => c.UserId == user.Id && c.ConsumedAt == null);
    }

    [Fact]
    public async Task StartAsync_UserAlreadyLinked_ReturnsAlreadyLinked()
    {
        var user = AddWebUser();
        user.TelegramId = 42;
        await Db.SaveChangesAsync();

        var (result, code, _) = await _sut.StartAsync(user.Id);

        result.Should().Be(StartLinkTelegramResult.AlreadyLinked);
        code.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_CalledTwice_InvalidatesPreviousCode()
    {
        var user = AddWebUser();
        var (_, firstCode, _) = await _sut.StartAsync(user.Id);
        var (_, secondCode, _) = await _sut.StartAsync(user.Id);

        var confirmWithOldCode = await _sut.ConfirmAsync(firstCode!, 700, "Someone", null);

        confirmWithOldCode.Should().Be(LinkTelegramResult.InvalidCode);
        (await _sut.ConfirmAsync(secondCode!, 700, "Someone", null)).Should().Be(LinkTelegramResult.Linked);
    }

    [Fact]
    public async Task ConfirmAsync_NoExistingTelegramUser_LinksDirectly()
    {
        var user = AddWebUser();
        var (_, code, _) = await _sut.StartAsync(user.Id);

        var result = await _sut.ConfirmAsync(code!, 701, "Ada Lovelace", "ada_handle");

        result.Should().Be(LinkTelegramResult.Linked);
        var updated = Db.Users.Single(u => u.Id == user.Id);
        updated.TelegramId.Should().Be(701);
        updated.TgUsername.Should().Be("ada_handle");
        Db.Users.Count().Should().Be(1, "не должно появиться отдельной Telegram-строки");
    }

    [Fact]
    public async Task ConfirmAsync_ExistingSeparateTelegramUser_MergesAccounts()
    {
        var webUser = AddWebUser();
        var telegramUser = new User
        {
            Id = Guid.NewGuid(),
            TelegramId = 702,
            TgUsername = "tg_handle",
            DisplayName = "Telegram User",
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(telegramUser);
        await Db.SaveChangesAsync();

        var (_, code, _) = await _sut.StartAsync(webUser.Id);
        var result = await _sut.ConfirmAsync(code!, 702, "Telegram User", "tg_handle");

        result.Should().Be(LinkTelegramResult.Merged);
        Db.Users.Should().NotContain(u => u.Id == telegramUser.Id);
        var survivor = Db.Users.Single(u => u.Id == webUser.Id);
        survivor.TelegramId.Should().Be(702);
        survivor.TgUsername.Should().Be("tg_handle");
    }

    [Fact]
    public async Task ConfirmAsync_InvalidCode_ReturnsInvalidCode()
    {
        var result = await _sut.ConfirmAsync("not-a-real-code", 703, "X", null);

        result.Should().Be(LinkTelegramResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmAsync_ExpiredCode_ReturnsInvalidCode()
    {
        var user = AddWebUser();
        var (_, code, _) = await _sut.StartAsync(user.Id);
        var stored = Db.TelegramLinkCodes.Single();
        stored.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await Db.SaveChangesAsync();

        var result = await _sut.ConfirmAsync(code!, 704, "X", null);

        result.Should().Be(LinkTelegramResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmAsync_CalledTwiceWithSameCode_SecondAttemptIsInvalidCode()
    {
        var user = AddWebUser();
        var (_, code, _) = await _sut.StartAsync(user.Id);

        (await _sut.ConfirmAsync(code!, 705, "X", null)).Should().Be(LinkTelegramResult.Linked);
        (await _sut.ConfirmAsync(code!, 706, "Y", null)).Should().Be(LinkTelegramResult.InvalidCode);
    }

    [Fact]
    public async Task PeekAsync_ValidCode_ReturnsMaskedEmail()
    {
        var user = AddWebUser("danil@example.com");
        var (_, code, _) = await _sut.StartAsync(user.Id);

        var peek = await _sut.PeekAsync(code!);

        peek.Should().NotBeNull();
        peek!.MaskedEmail.Should().Be("d***@example.com");
    }

    [Fact]
    public async Task PeekAsync_InvalidCode_ReturnsNull()
    {
        (await _sut.PeekAsync("bogus")).Should().BeNull();
    }
}
