using System.Text.RegularExpressions;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FamilyHub.Infrastructure.Security;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        _email.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                _lastSentBody = callInfo.ArgAt<EmailBody>(2).Text;
                return Task.CompletedTask;
            });
        var emailOptions = Options.Create(new EmailOptions { PublicSiteUrl = "https://test.familyhub.local" });
        var templates = new EmailTemplateRenderer(emailOptions);
        var otp = new EmailOtpService(Db, _email, templates, emailOptions, NullLogger<EmailOtpService>.Instance);
        _sut = new PwaAuthService(Db, otp, NullLogger<PwaAuthService>.Instance);
    }

    // Профиль (identity rework) — ФИО/ДР/пол обязательны на регистрации, никакого фолбэка
    // на локальную часть email больше нет (это было поведением DisplayName, удалённого поля).
    private const string TestLastName = "Иванов";
    private const string TestFirstName = "Иван";
    private static readonly DateOnly TestBirthDate = new(1990, 1, 1);

    private string LastCode() => Regex.Match(_lastSentBody!, @"\d{6}").Value;

    private async Task<(string Email, Guid UserId)> RegisterAsync(
        string email = "user@example.com", string password = "Passw0rd", string username = "testuser")
    {
        (await _sut.StartRegistrationAsync(email)).Should().Be(StartCodeResult.Sent);
        var (result, userId) = await _sut.ConfirmRegistrationAsync(
            email, LastCode(), password, username, TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
        result.Should().Be(ConfirmRegistrationResult.Success);
        return (email, userId);
    }

    [Fact]
    public async Task RegisterFlow_CreatesUserWithNormalizedEmailAndPassword()
    {
        await _sut.StartRegistrationAsync("  MiXeD@Example.COM ");
        var (result, userId) = await _sut.ConfirmRegistrationAsync(
            "mixed@example.com", LastCode(), "Str0ngPw", "mixeduser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);

        result.Should().Be(ConfirmRegistrationResult.Success);
        var user = Db.Users.Single(u => u.Id == userId);
        user.Email.Should().Be("mixed@example.com");
        user.Username.Should().Be("mixeduser");
        user.TelegramId.Should().BeNull();
        user.PasswordHash.Should().NotBeNull();
        user.LastName.Should().Be(TestLastName);
        user.FirstName.Should().Be(TestFirstName);
    }

    [Fact]
    public async Task ConfirmRegistration_WrongCodeFiveTimes_InvalidatesCode()
    {
        await _sut.StartRegistrationAsync("brute@example.com");
        var realCode = LastCode();

        for (var i = 0; i < 5; i++)
        {
            var (result, _) = await _sut.ConfirmRegistrationAsync("brute@example.com", "000000", "Passw0rd", "bruteuser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
            result.Should().Be(ConfirmRegistrationResult.InvalidCode);
        }

        // После 5 неверных попыток даже настоящий код недействителен.
        var (finalResult, _) = await _sut.ConfirmRegistrationAsync("brute@example.com", realCode, "Passw0rd", "bruteuser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
        finalResult.Should().Be(ConfirmRegistrationResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmRegistration_InvalidUsernameFormat_IsRejected_AndDoesNotConsumeCode()
    {
        await _sut.StartRegistrationAsync("badname@example.com");
        var code = LastCode();

        var (result, _) = await _sut.ConfirmRegistrationAsync("badname@example.com", code, "Passw0rd", "ab", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
        result.Should().Be(ConfirmRegistrationResult.InvalidUsername);

        // Код не потреблён — тот же код всё ещё годится с валидным username.
        var (retry, _) = await _sut.ConfirmRegistrationAsync("badname@example.com", code, "Passw0rd", "goodname", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
        retry.Should().Be(ConfirmRegistrationResult.Success);
    }

    [Fact]
    public async Task ConfirmRegistration_UsernameAlreadyTaken_IsRejected_AndDoesNotConsumeCode()
    {
        await RegisterAsync(email: "first@example.com", username: "takenname");

        await _sut.StartRegistrationAsync("second@example.com");
        var code = LastCode();

        var (result, _) = await _sut.ConfirmRegistrationAsync("second@example.com", code, "Passw0rd", "takenname", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
        result.Should().Be(ConfirmRegistrationResult.UsernameTaken);

        // Код не потреблён — тот же код всё ещё годится с другим username.
        var (retry, _) = await _sut.ConfirmRegistrationAsync("second@example.com", code, "Passw0rd", "freename", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
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

        var (result, _) = await _sut.ConfirmRegistrationAsync("late@example.com", LastCode(), "Passw0rd", "lateuser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);

        result.Should().Be(ConfirmRegistrationResult.InvalidCode);
    }

    [Theory]
    [InlineData("Sh0rt")] // короче 8 символов
    [InlineData("passw0rd")] // нет заглавной буквы
    [InlineData("PASSW0RD")] // нет строчной буквы
    [InlineData("PasswordOnly")] // нет цифры
    [InlineData("1234")] // старый формат PIN — регрессия: больше не годится для НОВОГО пароля
    public async Task ConfirmRegistration_WeakPassword_IsRejected(string weakPassword)
    {
        await _sut.StartRegistrationAsync("weak@example.com");

        var (result, _) = await _sut.ConfirmRegistrationAsync("weak@example.com", LastCode(), weakPassword, "weakuser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);

        result.Should().Be(ConfirmRegistrationResult.WeakPassword);
    }

    [Fact]
    public async Task ConfirmRegistration_EmptyLastName_IsRejected_AndDoesNotConsumeCode()
    {
        // Профиль валидируется ДО потребления OTP-кода (тот же принцип, что и username выше) —
        // пустая фамилия не должна сжигать 10-минутный код.
        await _sut.StartRegistrationAsync("noprofile@example.com");
        var code = LastCode();

        var (result, _) = await _sut.ConfirmRegistrationAsync(
            "noprofile@example.com", code, "Passw0rd", "noprofileuser", "  ", TestFirstName, null, TestBirthDate, Gender.Male);
        result.Should().Be(ConfirmRegistrationResult.InvalidProfile);

        // Код не потреблён — тот же код всё ещё годится с валидным профилем.
        var (retry, _) = await _sut.ConfirmRegistrationAsync(
            "noprofile@example.com", code, "Passw0rd", "noprofileuser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);
        retry.Should().Be(ConfirmRegistrationResult.Success);
    }

    [Fact]
    public async Task ConfirmRegistration_FutureBirthDate_IsRejected()
    {
        await _sut.StartRegistrationAsync("futuredate@example.com");

        var (result, _) = await _sut.ConfirmRegistrationAsync(
            "futuredate@example.com", LastCode(), "Passw0rd", "futureuser",
            TestLastName, TestFirstName, null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), Gender.Male);

        result.Should().Be(ConfirmRegistrationResult.InvalidProfile);
    }

    [Fact]
    public async Task Login_CorrectPassword_Succeeds_AndResetsFailures()
    {
        var (email, userId) = await RegisterAsync(password: "MyPass99");
        await _sut.LoginAsync(email, "Wr0ngPwd");

        var (result, user, _) = await _sut.LoginAsync(email, "MyPass99");

        result.Should().Be(LoginResult.Success);
        user!.Id.Should().Be(userId);
        Db.Users.Single(u => u.Id == userId).FailedLoginAttempts.Should().Be(0);
    }

    [Fact]
    public async Task Login_FiveWrongPasswords_LocksOut_AndCorrectPasswordIsRejectedWhileLocked()
    {
        var (email, userId) = await RegisterAsync(password: "MyPass99");

        for (var i = 0; i < 4; i++)
            (await _sut.LoginAsync(email, "Wr0ngPwd")).Result.Should().Be(LoginResult.InvalidCredentials);

        var fifth = await _sut.LoginAsync(email, "Wr0ngPwd");
        fifth.Result.Should().Be(LoginResult.LockedOut);
        fifth.LockedUntil.Should().BeAfter(DateTime.UtcNow);

        (await _sut.LoginAsync(email, "MyPass99")).Result.Should().Be(LoginResult.LockedOut);

        // Окно блокировки истекло → вход снова возможен.
        Db.Users.Single(u => u.Id == userId).LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        await Db.SaveChangesAsync();
        (await _sut.LoginAsync(email, "MyPass99")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task Login_UnknownEmail_InvalidCredentials()
    {
        (await _sut.LoginAsync("nobody@example.com", "Passw0rd")).Result.Should().Be(LoginResult.InvalidCredentials);
    }

    [Fact]
    public async Task Login_LegacyShortPassword_StillSucceeds()
    {
        // Учётки, чей пароль был установлен ДО перехода на политику PasswordRules (например,
        // ещё в формате старого 4-8-значного numeric PIN), не должны терять возможность войти —
        // LoginAsync намеренно не проверяет формат, только совпадение хеша. Это единственный
        // тест, который реально закрепляет эту гарантию обратной совместимости.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "legacy@example.com",
            PasswordHash = PasswordHasher.Hash("1234"),
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();

        var (result, loggedInUser, _) = await _sut.LoginAsync("legacy@example.com", "1234");

        result.Should().Be(LoginResult.Success);
        loggedInUser!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task LinkEmail_BindsEmailAndPasswordToExistingTelegramUser()
    {
        var telegramUser = Db.AddUser();

        (await _sut.StartLinkEmailAsync(telegramUser.Id, "linked@example.com")).Should().Be(StartCodeResult.Sent);
        var result = await _sut.ConfirmLinkEmailAsync(telegramUser.Id, "linked@example.com", LastCode(), "Link3dPw");

        result.Should().Be(LinkEmailResult.Success);
        var user = Db.Users.Single(u => u.Id == telegramUser.Id);
        user.Email.Should().Be("linked@example.com");
        user.PasswordHash.Should().NotBeNull();

        (await _sut.LoginAsync("linked@example.com", "Link3dPw")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task LinkEmail_CodeIssuedForAnotherUser_IsRejected()
    {
        var owner = Db.AddUser();
        var attacker = Db.AddUser();
        await _sut.StartLinkEmailAsync(owner.Id, "victim@example.com");

        var result = await _sut.ConfirmLinkEmailAsync(attacker.Id, "victim@example.com", LastCode(), "Passw0rd");

        result.Should().Be(LinkEmailResult.InvalidCode);
    }

    [Fact]
    public async Task ConfirmRegistration_EmailAlreadyRegistered_ReturnsEmailTaken()
    {
        var (email, _) = await RegisterAsync();

        await _sut.StartRegistrationAsync(email);
        var (result, _) = await _sut.ConfirmRegistrationAsync(email, LastCode(), "Passw0rd", "anotheruser", TestLastName, TestFirstName, null, TestBirthDate, Gender.Male);

        result.Should().Be(ConfirmRegistrationResult.EmailTaken);
    }

    [Fact]
    public async Task ResetPassword_ExistingAccount_SendsCode_AndChangesPasswordAfterConfirm()
    {
        var (email, userId) = await RegisterAsync(password: "First1Pw");

        (await _sut.StartResetPasswordAsync(email)).Should().Be(StartCodeResult.Sent);
        var (result, confirmedUserId) = await _sut.ConfirmResetPasswordAsync(email, LastCode(), "Second2Pw");

        result.Should().Be(ResetPasswordResult.Success);
        confirmedUserId.Should().Be(userId);
        (await _sut.LoginAsync(email, "First1Pw")).Result.Should().Be(LoginResult.InvalidCredentials, "старый пароль больше не действует");
        (await _sut.LoginAsync(email, "Second2Pw")).Result.Should().Be(LoginResult.Success);
    }

    [Fact]
    public async Task ResetPassword_UnknownEmail_StillReturnsSent_ButNoEmailIsActuallySent()
    {
        // Анти-enumeration в ответе — но письмо реально не уходит на несуществующий аккаунт
        // (в отличие от register/start, где письмо уместно всегда).
        (await _sut.StartResetPasswordAsync("nobody@example.com")).Should().Be(StartCodeResult.Sent);

        await _email.DidNotReceive().SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetPassword_UnlocksAccount_LockedOutByFailedLogins()
    {
        var (email, userId) = await RegisterAsync(password: "Third3Pw");
        for (var i = 0; i < 5; i++)
            await _sut.LoginAsync(email, "Wr0ngPwd");
        Db.Users.Single(u => u.Id == userId).LockedUntil.Should().NotBeNull();

        await _sut.StartResetPasswordAsync(email);
        (await _sut.ConfirmResetPasswordAsync(email, LastCode(), "Fourth4Pw")).Result.Should().Be(ResetPasswordResult.Success);

        var user = Db.Users.Single(u => u.Id == userId);
        user.LockedUntil.Should().BeNull();
        user.FailedLoginAttempts.Should().Be(0);
        (await _sut.LoginAsync(email, "Fourth4Pw")).Result.Should().Be(LoginResult.Success);
    }

    [Theory]
    [InlineData("Sh0rt")]
    [InlineData("passw0rd")]
    [InlineData("PASSW0RD")]
    [InlineData("PasswordOnly")]
    [InlineData("1234")]
    public async Task ResetPassword_WeakNewPassword_IsRejected(string weakPassword)
    {
        var (email, _) = await RegisterAsync(password: "Fifth5Pw");
        await _sut.StartResetPasswordAsync(email);
        var code = LastCode();

        (await _sut.ConfirmResetPasswordAsync(email, code, weakPassword)).Result.Should().Be(ResetPasswordResult.WeakPassword);
    }

    [Fact]
    public async Task ResetPassword_WeakNewPassword_DoesNotConsumeCode()
    {
        var (email, _) = await RegisterAsync(password: "Sixth6Pw");
        await _sut.StartResetPasswordAsync(email);
        var code = LastCode();

        (await _sut.ConfirmResetPasswordAsync(email, code, "Sh0rt")).Result.Should().Be(ResetPasswordResult.WeakPassword);

        // Код не потреблён — тот же код всё ещё годится с валидным паролем.
        (await _sut.ConfirmResetPasswordAsync(email, code, "Seventh7")).Result.Should().Be(ResetPasswordResult.Success);
    }

    [Fact]
    public async Task ResetPassword_WrongCode_IsRejected()
    {
        var (email, _) = await RegisterAsync(password: "Eighth8Pw");
        await _sut.StartResetPasswordAsync(email);

        (await _sut.ConfirmResetPasswordAsync(email, "000000", "Ninth9Pw")).Result.Should().Be(ResetPasswordResult.InvalidCode);
        (await _sut.LoginAsync(email, "Eighth8Pw")).Result.Should().Be(LoginResult.Success, "старый пароль всё ещё действует");
    }
}
