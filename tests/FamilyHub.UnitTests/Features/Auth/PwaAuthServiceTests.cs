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

    private async Task<(string Email, Guid UserId)> RegisterAsync(
        string email = "user@example.com", string pin = "1234", string username = "testuser")
    {
        (await _sut.StartRegistrationAsync(email)).Should().Be(StartCodeResult.Sent);
        var (result, userId) = await _sut.ConfirmRegistrationAsync(email, LastCode(), pin, username, "Тестовый");
        result.Should().Be(ConfirmRegistrationResult.Success);
        return (email, userId);
    }

    [Fact]
    public async Task RegisterFlow_CreatesUserWithNormalizedEmailAndPin()
    {
        await _sut.StartRegistrationAsync("  MiXeD@Example.COM ");
        var (result, userId) = await _sut.ConfirmRegistrationAsync("mixed@example.com", LastCode(), "5678", "mixeduser", null);

        result.Should().Be(ConfirmRegistrationResult.Success);
        var user = Db.Users.Single(u => u.Id == userId);
        user.Email.Should().Be("mixed@example.com");
        user.Username.Should().Be("mixeduser");
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
            var (result, _) = await _sut.ConfirmRegistrationAsync("brute@example.com", "000000", "1234", "bruteuser", null);
            result.Should().Be(ConfirmRegistrationResult.InvalidCode);
        }

        // После 5 неверных попыток даже настоящий код недействителен.
        var (finalResult, _) = await _sut.ConfirmRegistrationAsync("brute@example.com", realCode, "1234", "bruteuser", null);
        finalResult.Should().Be(ConfirmRegistrationResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmRegistration_InvalidUsernameFormat_IsRejected_AndDoesNotConsumeCode()
    {
        await _sut.StartRegistrationAsync("badname@example.com");
        var code = LastCode();

        var (result, _) = await _sut.ConfirmRegistrationAsync("badname@example.com", code, "1234", "ab", null);
        result.Should().Be(ConfirmRegistrationResult.InvalidUsername);

        // Код не потреблён — тот же код всё ещё годится с валидным username.
        var (retry, _) = await _sut.ConfirmRegistrationAsync("badname@example.com", code, "1234", "goodname", null);
        retry.Should().Be(ConfirmRegistrationResult.Success);
    }

    [Fact]
    public async Task ConfirmRegistration_UsernameAlreadyTaken_IsRejected_AndDoesNotConsumeCode()
    {
        await RegisterAsync(email: "first@example.com", username: "takenname");

        await _sut.StartRegistrationAsync("second@example.com");
        var code = LastCode();

        var (result, _) = await _sut.ConfirmRegistrationAsync("second@example.com", code, "1234", "takenname", null);
        result.Should().Be(ConfirmRegistrationResult.UsernameTaken);

        // Код не потреблён — тот же код всё ещё годится с другим username.
        var (retry, _) = await _sut.ConfirmRegistrationAsync("second@example.com", code, "1234", "freename", null);
        retry.Should().Be(ConfirmRegistrationResult.Success);
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

        var (result, _) = await _sut.ConfirmRegistrationAsync("late@example.com", LastCode(), "1234", "lateuser", null);

        result.Should().Be(ConfirmRegistrationResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmRegistration_WeakPin_IsRejected()
    {
        await _sut.StartRegistrationAsync("weak@example.com");

        var (result, _) = await _sut.ConfirmRegistrationAsync("weak@example.com", LastCode(), "12", "weakuser", null);

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
        var (result, _) = await _sut.ConfirmRegistrationAsync(email, LastCode(), "1234", "anotheruser", null);

        result.Should().Be(ConfirmRegistrationResult.EmailTaken);
    }

    [Fact]
    public async Task ResetPin_ExistingAccount_SendsCode_AndChangesPinAfterConfirm()
    {
        var (email, userId) = await RegisterAsync(pin: "1111");

        (await _sut.StartResetPinAsync(email)).Should().Be(StartCodeResult.Sent);
        var (result, confirmedUserId) = await _sut.ConfirmResetPinAsync(email, LastCode(), "2222");

        result.Should().Be(ResetPinResult.Success);
        confirmedUserId.Should().Be(userId);
        (await _sut.LoginAsync(email, "1111")).Result.Should().Be(LoginResult.InvalidCredentials, "старый PIN больше не действует");
        (await _sut.LoginAsync(email, "2222")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task ResetPin_UnknownEmail_StillReturnsSent_ButNoEmailIsActuallySent()
    {
        // Анти-enumeration в ответе — но письмо реально не уходит на несуществующий аккаунт
        // (в отличие от register/start, где письмо уместно всегда).
        (await _sut.StartResetPinAsync("nobody@example.com")).Should().Be(StartCodeResult.Sent);

        await _email.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetPin_UnlocksAccount_LockedOutByFailedPins()
    {
        var (email, userId) = await RegisterAsync(pin: "3333");
        for (var i = 0; i < 5; i++)
            await _sut.LoginAsync(email, "0000");
        Db.Users.Single(u => u.Id == userId).LockedUntil.Should().NotBeNull();

        await _sut.StartResetPinAsync(email);
        (await _sut.ConfirmResetPinAsync(email, LastCode(), "4444")).Result.Should().Be(ResetPinResult.Success);

        var user = Db.Users.Single(u => u.Id == userId);
        user.LockedUntil.Should().BeNull();
        user.FailedPinAttempts.Should().Be(0);
        (await _sut.LoginAsync(email, "4444")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task ResetPin_WeakNewPin_IsRejected_AndDoesNotConsumeCode()
    {
        var (email, _) = await RegisterAsync(pin: "5555");
        await _sut.StartResetPinAsync(email);
        var code = LastCode();

        (await _sut.ConfirmResetPinAsync(email, code, "12")).Result.Should().Be(ResetPinResult.WeakPin);

        // Код не потреблён — тот же код всё ещё годится с валидным PIN.
        (await _sut.ConfirmResetPinAsync(email, code, "6666")).Result.Should().Be(ResetPinResult.Success);
    }

    [Fact]
    public async Task ResetPin_WrongCode_IsRejected()
    {
        var (email, _) = await RegisterAsync(pin: "7777");
        await _sut.StartResetPinAsync(email);

        (await _sut.ConfirmResetPinAsync(email, "000000", "8888")).Result.Should().Be(ResetPinResult.InvalidCode);
        (await _sut.LoginAsync(email, "7777")).Result.Should().Be(LoginResult.Success, "старый PIN всё ещё действует");
    }
}
