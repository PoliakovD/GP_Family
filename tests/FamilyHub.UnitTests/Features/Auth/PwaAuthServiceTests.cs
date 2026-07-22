using System.Text.RegularExpressions;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Infrastructure.Email;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Features.Auth;

public class PwaAuthServiceTests : SqliteTestBase
{
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly PwaAuthService _sut;
    private string? _lastSentBody;

    public PwaAuthServiceTests()
    {
        _email.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                _lastSentBody = callInfo.ArgAt<string>(2);
                return Task.CompletedTask;
            });
        _sut = new PwaAuthService(Db, _email, NullLogger<PwaAuthService>.Instance);
    }

    private string LastCode() => Regex.Match(_lastSentBody!, @"\d{6}").Value;

    private async Task<(string Email, Guid UserId)> RegisterAsync(string email = "user@example.com", string pin = "1234")
    {
        (await _sut.StartRegistrationAsync(email)).Should().Be(StartCodeResult.Sent);
        var (result, userId) = await _sut.ConfirmRegistrationAsync(email, LastCode(), pin, "Тестовый");
        result.Should().Be(ConfirmRegistrationResult.Success);
        return (email, userId);
    }

    [Fact]
    public async Task RegisterFlow_CreatesUserWithNormalizedEmailAndPin()
    {
        await _sut.StartRegistrationAsync("  MiXeD@Example.COM ");
        var (result, userId) = await _sut.ConfirmRegistrationAsync("mixed@example.com", LastCode(), "5678", null);

        result.Should().Be(ConfirmRegistrationResult.Success);
        var user = Db.Users.Single(u => u.Id == userId);
        user.Email.Should().Be("mixed@example.com");
        user.TelegramId.Should().BeNull();
        user.PinHash.Should().NotBeNull();
        user.DisplayName.Should().Be("mixed", "имя по умолчанию — локальная часть адреса");
    }

    [Fact]
    public async Task ConfirmRegistration_WrongCodeFiveTimes_InvalidatesCode()
    {
        await _sut.StartRegistrationAsync("brute@example.com");
        var realCode = LastCode();

        for (var i = 0; i < 5; i++)
        {
            var (result, _) = await _sut.ConfirmRegistrationAsync("brute@example.com", "000000", "1234", null);
            result.Should().Be(ConfirmRegistrationResult.InvalidCode);
        }

        // После 5 неверных попыток даже настоящий код недействителен.
        var (finalResult, _) = await _sut.ConfirmRegistrationAsync("brute@example.com", realCode, "1234", null);
        finalResult.Should().Be(ConfirmRegistrationResult.InvalidCode);
    }

    [Fact]
    public async Task StartRegistration_FourthCodeInAnHour_IsThrottled()
    {
        for (var i = 0; i < 3; i++)
            (await _sut.StartRegistrationAsync("spam@example.com")).Should().Be(StartCodeResult.Sent);

        (await _sut.StartRegistrationAsync("spam@example.com")).Should().Be(StartCodeResult.Throttled);
    }

    [Fact]
    public async Task ConfirmRegistration_ExpiredCode_IsRejected()
    {
        await _sut.StartRegistrationAsync("late@example.com");
        var code = Db.EmailVerificationCodes.Single();
        code.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await Db.SaveChangesAsync();

        var (result, _) = await _sut.ConfirmRegistrationAsync("late@example.com", LastCode(), "1234", null);

        result.Should().Be(ConfirmRegistrationResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmRegistration_WeakPin_IsRejected()
    {
        await _sut.StartRegistrationAsync("weak@example.com");

        var (result, _) = await _sut.ConfirmRegistrationAsync("weak@example.com", LastCode(), "12", null);

        result.Should().Be(ConfirmRegistrationResult.WeakPin);
    }

    [Fact]
    public async Task Login_CorrectPin_Succeeds_AndResetsFailures()
    {
        var (email, userId) = await RegisterAsync(pin: "4321");
        await _sut.LoginAsync(email, "0000");

        var (result, user, _) = await _sut.LoginAsync(email, "4321");

        result.Should().Be(LoginResult.Success);
        user!.Id.Should().Be(userId);
        Db.Users.Single(u => u.Id == userId).FailedPinAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Login_FiveWrongPins_LocksOut_AndCorrectPinIsRejectedWhileLocked()
    {
        var (email, userId) = await RegisterAsync(pin: "4321");

        for (var i = 0; i < 4; i++)
            (await _sut.LoginAsync(email, "0000")).Result.Should().Be(LoginResult.InvalidCredentials);

        var fifth = await _sut.LoginAsync(email, "0000");
        fifth.Result.Should().Be(LoginResult.LockedOut);
        fifth.LockedUntil.Should().BeAfter(DateTime.UtcNow);

        (await _sut.LoginAsync(email, "4321")).Result.Should().Be(LoginResult.LockedOut);

        // Окно блокировки истекло → вход снова возможен.
        Db.Users.Single(u => u.Id == userId).LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        await Db.SaveChangesAsync();
        (await _sut.LoginAsync(email, "4321")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task Login_UnknownEmail_InvalidCredentials()
    {
        (await _sut.LoginAsync("nobody@example.com", "1234")).Result.Should().Be(LoginResult.InvalidCredentials);
    }

    [Fact]
    public async Task LinkEmail_BindsEmailAndPinToExistingTelegramUser()
    {
        var telegramUser = Db.AddUser();

        (await _sut.StartLinkEmailAsync(telegramUser.Id, "linked@example.com")).Should().Be(StartCodeResult.Sent);
        var result = await _sut.ConfirmLinkEmailAsync(telegramUser.Id, "linked@example.com", LastCode(), "9876");

        result.Should().Be(LinkEmailResult.Success);
        var user = Db.Users.Single(u => u.Id == telegramUser.Id);
        user.Email.Should().Be("linked@example.com");
        user.PinHash.Should().NotBeNull();

        (await _sut.LoginAsync("linked@example.com", "9876")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task LinkEmail_CodeIssuedForAnotherUser_IsRejected()
    {
        var owner = Db.AddUser();
        var attacker = Db.AddUser();
        await _sut.StartLinkEmailAsync(owner.Id, "victim@example.com");

        var result = await _sut.ConfirmLinkEmailAsync(attacker.Id, "victim@example.com", LastCode(), "1234");

        result.Should().Be(LinkEmailResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmRegistration_EmailAlreadyRegistered_ReturnsEmailTaken()
    {
        var (email, _) = await RegisterAsync();

        await _sut.StartRegistrationAsync(email);
        var (result, _) = await _sut.ConfirmRegistrationAsync(email, LastCode(), "1234", null);

        result.Should().Be(ConfirmRegistrationResult.EmailTaken);
    }
}
