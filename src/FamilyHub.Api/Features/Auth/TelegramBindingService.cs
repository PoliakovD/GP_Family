using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Auth;

public enum TelegramInitResult { Bound, BindingRequired, InvalidInitData }

public enum TelegramSendCodeResult { Sent, Throttled, InvalidInitData }

public enum TelegramBindResult { Success, InvalidCode, InvalidInitData, EmailLinkedToDifferentTelegram, TelegramAlreadyBound }

/// <summary>
/// Привязка Telegram Mini App к email-аккаунту — единственный способ превратить голый
/// TelegramId в рабочий User (см. TelegramMiniAppAuthenticationHandler: lookup-only, ничего
/// не создаёт сам). Никаких токенов/сессий здесь не выдаётся — у Telegram Mini App нет сессии
/// вообще, initData проверяется заново на каждый обычный запрос; после успешного bind
/// последующие запросы с тем же TelegramId просто проходят существующий per-request хендлер.
///
/// Пароль от пользователя здесь НЕ запрашивается — сама форма привязки состоит только из
/// email + кода подтверждения. Если email оказывается новым (или, в редком защитном случае,
/// принадлежит существующей записи без пароля), сервис сам генерирует временный пароль,
/// сохраняет его хеш и отправляет пароль на почту — иначе аккаунт, созданный только через
/// Telegram, никогда не смог бы войти в PWA (PwaAuthService.LoginAsync требует
/// PasswordHash != null). Сменить временный пароль пользователь может обычным
/// "Забыли пароль?" — отдельного UI для этого не заводим.
/// </summary>
public class TelegramBindingService(
    AppDbContext db, EmailOtpService otp, ITelegramInitDataValidator validator, IEmailSender email,
    EmailTemplateRenderer templates, IOptions<EmailOptions> emailOptions, ILogger<TelegramBindingService> logger)
{
    /// <summary>Копирайт рамки письма с временным паролем. Public static — как EmailOtpService.CopyFor,
    /// переиспользуется EmailPreviewWriter/dev-эндпоинтом предпросмотра без похода в БД.</summary>
    public static EmailLayoutCopy TemporaryPasswordCopy(string email) => new(
        "Пароль для входа с сайта",
        "Временный пароль для входа на сайте",
        $"Аккаунт FamilyHub с адресом {email} создан через Telegram — на сайте в него можно войти с временным паролем.",
        "Сменить пароль можно на странице входа — «Забыли пароль?».");


    public async Task<TelegramInitResult> InitAsync(string initData, CancellationToken ct = default)
    {
        var result = validator.Validate(initData);
        if (result is null) return TelegramInitResult.InvalidInitData;

        var bound = await db.Users.AnyAsync(u => u.TelegramId == result.TelegramId, ct);
        return bound ? TelegramInitResult.Bound : TelegramInitResult.BindingRequired;
    }

    public async Task<TelegramSendCodeResult> SendCodeAsync(string rawEmail, string initData, CancellationToken ct = default)
    {
        // Повторная валидация initData — тот же Telegram-идентификатор, что запросил
        // BindingRequired, а не произвольный вызов с чужим/просроченным initData.
        if (validator.Validate(initData) is null) return TelegramSendCodeResult.InvalidInitData;

        var normalizedEmail = PwaAuthService.NormalizeEmail(rawEmail);
        var result = await otp.IssueCodeAsync(normalizedEmail, EmailCodePurpose.TelegramBind, userId: null, ct);
        return result switch
        {
            StartCodeResult.Throttled => TelegramSendCodeResult.Throttled,
            _ => TelegramSendCodeResult.Sent,
        };
    }

    // Имя НЕ должно быть BindAsync: ASP.NET Core Minimal API резервирует статический метод
    // "BindAsync(HttpContext, ParameterInfo)" как конвенцию кастомного парамет-байндинга и
    // сканирует по имени ЛЮБОЙ тип параметра эндпоинта — включая DI-сервисы. Инстанс-метод
    // с этим именем ловится той же проверкой и валит запуск хоста с InvalidOperationException
    // ("BindAsync method found ... incorrect format"), даже когда сервис используется только
    // через DI, а не как параметр модели.
    public async Task<(TelegramBindResult Result, bool ProfileRequired)> ConfirmBindAsync(
        string rawEmail, string code, string initData, CancellationToken ct = default)
    {
        var initResult = validator.Validate(initData);
        if (initResult is null) return (TelegramBindResult.InvalidInitData, false);

        var normalizedEmail = PwaAuthService.NormalizeEmail(rawEmail);
        var verification = await otp.ConsumeCodeAsync(normalizedEmail, code, EmailCodePurpose.TelegramBind, ct);
        if (verification is null) return (TelegramBindResult.InvalidCode, false);

        var telegramId = initResult.TelegramId;

        // Гонка: этот TelegramId мог быть привязан где-то ещё в промежутке между /init и /bind.
        if (await db.Users.AnyAsync(u => u.TelegramId == telegramId, ct))
        {
            await db.SaveChangesAsync(ct); // код всё равно потреблён
            return (TelegramBindResult.TelegramAlreadyBound, false);
        }

        var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        User user;
        if (existingUser is not null)
        {
            if (existingUser.TelegramId is not null)
            {
                await db.SaveChangesAsync(ct);
                return (TelegramBindResult.EmailLinkedToDifferentTelegram, false);
            }

            existingUser.TelegramId = telegramId;
            if (string.IsNullOrWhiteSpace(existingUser.TgUsername))
                existingUser.TgUsername = initResult.Username;
            user = existingUser;
        }
        else
        {
            // ФИО/ДР/пол здесь НЕ заполняются из Telegram initData — профиль (identity rework)
            // собирается отдельным экраном ПОСЛЕ привязки (см. ProfileRequired ниже и
            // TelegramBindComponent.confirmBind на фронте), тем же путём, что и для брошенной
            // на середине PWA-регистрации.
            user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                TelegramId = telegramId,
                TgUsername = initResult.Username,
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
        }

        // Покрывает и "новый аккаунт" (PasswordHash всегда null у только что созданного User),
        // и защитный случай "существующая запись без пароля" — без хеша PwaAuthService.LoginAsync
        // никогда не смог бы её аутентифицировать иначе как через Telegram. Аккаунт с уже
        // существующим паролем (обычный PWA-пользователь, привязывающий Telegram) сюда не
        // попадает — его пароль этой формой никогда не трогается.
        string? temporaryPassword = null;
        if (user.PasswordHash is null)
        {
            temporaryPassword = TemporaryPasswordGenerator.Generate();
            user.PasswordHash = PasswordHasher.Hash(temporaryPassword);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Уникальный индекс на TelegramId поймал гонку, не замеченную проверкой выше.
            logger.LogDebug(ex, "Гонка при привязке Telegram {TelegramId} — уже занят параллельно", telegramId);
            return (TelegramBindResult.TelegramAlreadyBound, false);
        }

        // Письмо — ПОСЛЕ успешного сохранения (иначе можно отправить пароль для строки,
        // которая не была записана из-за проигранной гонки), и с широким catch: сбой
        // почтового провайдера ИЛИ рендера шаблона не должен блокировать уже выданный доступ
        // из Telegram — он работает вне зависимости от письма. Восстановить вход в PWA всегда
        // можно через "Забыли пароль?" позже. Именно поэтому рендер HTML — тоже внутри try:
        // опечатка в плейсхолдере шаблона не должна ронять привязку Telegram целиком (ловим
        // такие опечатки тестами, не аварийным путём в проде).
        if (temporaryPassword is not null)
        {
            try
            {
                var copy = TemporaryPasswordCopy(normalizedEmail);
                var html = templates.RenderTemporaryPassword(copy, normalizedEmail, temporaryPassword);

                // Текстовая часть — строки с паролем сохранены ДОСЛОВНО: на них опирается
                // CapturingEmailSender.LastTemporaryPasswordFor (регулярка "пароль для входа на
                // сайте: (\S+)"). Ссылка на сайт — ПОСЛЕ них, чтобы (\S+) по-прежнему
                // останавливался на самом пароле.
                var text =
                    $"Аккаунт FamilyHub с адресом {normalizedEmail} создан через Telegram.\n" +
                    $"Ваш временный пароль для входа на сайте: {temporaryPassword}\n" +
                    "Сменить его можно на странице входа — «Забыли пароль?».\n\n" +
                    $"Открыть FamilyHub: {emailOptions.Value.PublicSiteUrl}";

                await email.SendAsync(normalizedEmail, "FamilyHub: пароль для входа с сайта", new EmailBody(text, html), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось отправить временный пароль на адрес (hash {EmailHash})",
                    TokenHasher.Hash(normalizedEmail)[..8]);
            }
        }

        logger.LogInformation("Telegram {TelegramId} привязан к аккаунту {UserId}", telegramId, user.Id);
        var profileRequired = !PersonName.IsCompleteProfile(user.LastName, user.FirstName, user.BirthDate, user.Gender);
        return (TelegramBindResult.Success, profileRequired);
    }
}
