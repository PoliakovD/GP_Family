using System.Text.RegularExpressions;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Telegram;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Features.Auth;

/// <summary>
/// Привязка Telegram Mini App к email-аккаунту: единственный способ снабдить TelegramId
/// работающим User (см. TelegramMiniAppAuthenticationHandlerTests — lookup-only, сам не создаёт).
/// Форма — только email + код; пароль пользователь здесь не вводит (см. класс-doc
/// TelegramBindingService) — эти тесты проверяют, что сервис сам генерирует и рассылает
/// временный пароль ровно тогда, когда он нужен, и не рассылает, когда не нужен.
/// </summary>
public class TelegramBindingServiceTests : SqliteTestBase
{
    private const string ValidInitData = "valid-init-data";

    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly ITelegramInitDataValidator _validator = Substitute.For<ITelegramInitDataValidator>();
    private readonly TelegramBindingService _sut;
    private readonly List<(string To, string Body)> _sent = [];

    public TelegramBindingServiceTests()
    {
        _email.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                _sent.Add((callInfo.ArgAt<string>(0), callInfo.ArgAt<EmailBody>(2).Text));
                return Task.CompletedTask;
            });
        var emailOptions = Options.Create(new EmailOptions { PublicSiteUrl = "https://test.familyhub.local" });
        var templates = new EmailTemplateRenderer(emailOptions);
        var otp = new EmailOtpService(Db, _email, templates, emailOptions, NullLogger<EmailOtpService>.Instance);
        _sut = new TelegramBindingService(
            Db, otp, _validator, _email, templates, emailOptions, NullLogger<TelegramBindingService>.Instance);
    }

    private void SetupValidInitData(long telegramId, string? displayName = null, string? username = null) =>
        _validator.Validate(ValidInitData).Returns(new TelegramInitDataResult(telegramId, displayName, username));

    /// <summary>Последний шестизначный OTP-код среди писем на адрес (сканирует назад — на тот
    /// же адрес позже мог уйти ещё и временный пароль).</summary>
    private string LastCode(string email) =>
        _sent.Where(m => m.To == email).Reverse()
            .Select(m => Regex.Match(m.Body, @"\d{6}"))
            .First(m => m.Success).Value;

    /// <summary>Последний временный пароль, отправленный на адрес; null — такого письма не было.</summary>
    private string? LastTemporaryPassword(string email) =>
        _sent.Where(m => m.To == email).Reverse()
            .Select(m => Regex.Match(m.Body, @"пароль для входа на сайте: (\S+)"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;

    private User AddUser(string? email = null, long? telegramId = null, bool withPassword = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            TelegramId = telegramId,
            PasswordHash = email is not null && withPassword ? "hash" : null,
            CreatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task InitAsync_InvalidInitData_ReturnsInvalidInitData()
    {
        _validator.Validate("garbage").Returns((TelegramInitDataResult?)null);

        (await _sut.InitAsync("garbage")).Should().Be(TelegramInitResult.InvalidInitData);
    }

    [Fact]
    public async Task InitAsync_TelegramIdNotBound_ReturnsBindingRequired()
    {
        SetupValidInitData(100);

        (await _sut.InitAsync(ValidInitData)).Should().Be(TelegramInitResult.BindingRequired);
    }

    [Fact]
    public async Task InitAsync_TelegramIdAlreadyBound_ReturnsBound()
    {
        SetupValidInitData(101);
        AddUser(email: "x@example.com", telegramId: 101);

        (await _sut.InitAsync(ValidInitData)).Should().Be(TelegramInitResult.Bound);
    }

    [Fact]
    public async Task BindAsync_EmailMatchesExistingPwaAccount_AttachesTelegramIdToSameUser_DoesNotTouchExistingPassword_AndSendsNoExtraEmail()
    {
        var pwaUser = AddUser(email: "danil@example.com");
        var originalPasswordHash = pwaUser.PasswordHash;
        SetupValidInitData(200, "Danil TG", "danil_tg");
        await _sut.SendCodeAsync("danil@example.com", ValidInitData);
        var code = LastCode("danil@example.com");
        _sent.Clear(); // письмо с OTP-кодом больше не интересно — проверяем именно ОТСУТСТВИЕ следующего

        var (result, _) = await _sut.ConfirmBindAsync("danil@example.com", code, ValidInitData);

        result.Should().Be(TelegramBindResult.Success);
        Db.Users.Count().Should().Be(1, "должен привязаться к существующему аккаунту, а не создать новый");
        var updated = Db.Users.Single(u => u.Id == pwaUser.Id);
        updated.TelegramId.Should().Be(200);
        updated.TgUsername.Should().Be("danil_tg");
        updated.PasswordHash.Should().Be(originalPasswordHash, "пароль уже существующего PWA-аккаунта не должен меняться при привязке Telegram");
        _sent.Should().BeEmpty("у аккаунта уже есть пароль — привязка не должна слать ничего дополнительно");
    }

    [Fact]
    public async Task BindAsync_NewEmail_CreatesUser_WithGeneratedTemporaryPassword_AndEmailsIt()
    {
        SetupValidInitData(201, "New Guy", "newguy");
        await _sut.SendCodeAsync("newperson@example.com", ValidInitData);
        var code = LastCode("newperson@example.com");

        var (result, profileRequired) = await _sut.ConfirmBindAsync("newperson@example.com", code, ValidInitData);

        result.Should().Be(TelegramBindResult.Success);
        // ФИО/ДР/пол (identity rework) НЕ заполняются из Telegram initData — профиль собирается
        // отдельным экраном после привязки (см. profileGuard на фронте).
        profileRequired.Should().BeTrue();
        var created = Db.Users.Single(u => u.TelegramId == 201);
        created.Email.Should().Be("newperson@example.com");
        created.LastName.Should().BeNull();
        created.FirstName.Should().BeNull();
        created.PasswordHash.Should().NotBeNull("без пароля новый аккаунт не смог бы потом войти в PWA");

        var temporaryPassword = LastTemporaryPassword("newperson@example.com");
        temporaryPassword.Should().NotBeNullOrEmpty();
        PasswordRules.IsValid(temporaryPassword!).Should().BeTrue();
        PasswordHasher.Verify(temporaryPassword!, created.PasswordHash!).Should().BeTrue();
    }

    [Fact]
    public async Task BindAsync_ExistingUserWithoutPassword_GeneratesAndEmailsTemporaryPassword()
    {
        // Защитный случай: строка User с Email, но без пароля — сегодня таким путём (через
        // обычные флоу) не создаётся, но если когда-либо возникнет (легаси-данные), bind должен
        // её долечить, а не оставить аккаунт без единого способа войти в PWA.
        var orphan = AddUser(email: "orphan@example.com", withPassword: false);
        SetupValidInitData(210, "Orphan TG", "orphan_tg");
        await _sut.SendCodeAsync("orphan@example.com", ValidInitData);
        var code = LastCode("orphan@example.com");

        var (result, _) = await _sut.ConfirmBindAsync("orphan@example.com", code, ValidInitData);

        result.Should().Be(TelegramBindResult.Success);
        var updated = Db.Users.Single(u => u.Id == orphan.Id);
        updated.PasswordHash.Should().NotBeNull();
        var temporaryPassword = LastTemporaryPassword("orphan@example.com");
        temporaryPassword.Should().NotBeNullOrEmpty();
        PasswordHasher.Verify(temporaryPassword!, updated.PasswordHash!).Should().BeTrue();
    }

    [Fact]
    public async Task BindAsync_EmailSenderThrows_StillReturnsSuccess()
    {
        // Нагруженная гарантия: сбой почтового провайдера НЕ должен блокировать уже выданный
        // доступ из Telegram — тот работает независимо от письма с временным паролем.
        SetupValidInitData(211, "Resilient", "resilient_tg");
        await _sut.SendCodeAsync("resilient@example.com", ValidInitData);
        var code = LastCode("resilient@example.com");
        _email.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("SMTP недоступен")));

        var (result, _) = await _sut.ConfirmBindAsync("resilient@example.com", code, ValidInitData);

        result.Should().Be(TelegramBindResult.Success);
        Db.Users.Single(u => u.TelegramId == 211).PasswordHash.Should().NotBeNull();
    }

    [Fact]
    public async Task BindAsync_InvalidCode_ReturnsInvalidCode()
    {
        SetupValidInitData(202);

        (await _sut.ConfirmBindAsync("someone@example.com", "000000", ValidInitData)).Result.Should().Be(TelegramBindResult.InvalidCode);
    }

    [Fact]
    public async Task BindAsync_InvalidInitData_ReturnsInvalidInitData()
    {
        _validator.Validate("garbage").Returns((TelegramInitDataResult?)null);

        (await _sut.ConfirmBindAsync("someone@example.com", "123456", "garbage")).Result.Should().Be(TelegramBindResult.InvalidInitData);
    }

    [Fact]
    public async Task BindAsync_EmailAlreadyLinkedToDifferentTelegram_ReturnsConflict()
    {
        AddUser(email: "taken@example.com", telegramId: 500);
        SetupValidInitData(501);
        await _sut.SendCodeAsync("taken@example.com", ValidInitData);
        var code = LastCode("taken@example.com");

        (await _sut.ConfirmBindAsync("taken@example.com", code, ValidInitData)).Result.Should().Be(TelegramBindResult.EmailLinkedToDifferentTelegram);
    }

    [Fact]
    public async Task BindAsync_TelegramIdAlreadyBoundElsewhere_ReturnsConflict()
    {
        AddUser(email: "existing@example.com", telegramId: 600);
        SetupValidInitData(600); // тот же TelegramId, что уже занят
        await _sut.SendCodeAsync("newmail@example.com", ValidInitData);
        var code = LastCode("newmail@example.com");

        (await _sut.ConfirmBindAsync("newmail@example.com", code, ValidInitData)).Result.Should().Be(TelegramBindResult.TelegramAlreadyBound);
    }

    [Fact]
    public async Task BindAsync_CalledTwiceWithSameCode_SecondAttemptIsInvalidCode()
    {
        SetupValidInitData(700);
        await _sut.SendCodeAsync("repeat@example.com", ValidInitData);
        var code = LastCode("repeat@example.com");

        (await _sut.ConfirmBindAsync("repeat@example.com", code, ValidInitData)).Result.Should().Be(TelegramBindResult.Success);
        (await _sut.ConfirmBindAsync("repeat@example.com", code, ValidInitData)).Result.Should().Be(TelegramBindResult.InvalidCode);
    }
}
