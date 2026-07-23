using FamilyHub.Domain.Entities;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.CurrentUser;

public class UserProvisioningService(AppDbContext db, ILogger<UserProvisioningService> logger) : IUserProvisioningService
{
    public async Task<Guid> GetOrCreateUserIdAsync(long telegramId, string? displayName, string? username = null, CancellationToken ct = default)
    {
        var existing = await db.Users
            .Where(u => u.TelegramId == telegramId)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            // Имя и хэндл в Telegram могут меняться — держим TgUsername свежим на каждый логин.
            // Видимый (app) Username НЕ трогаем — это отдельный, назначаемый пользователем
            // идентификатор, обновление профиля из Telegram не должно его перезаписывать/угонять.
            var changed = false;
            if (!string.IsNullOrWhiteSpace(displayName) && existing.DisplayName != displayName)
            {
                existing.DisplayName = displayName;
                changed = true;
            }
            if (existing.TgUsername != username)
            {
                existing.TgUsername = username;
                changed = true;
            }
            if (changed)
            {
                await db.SaveChangesAsync(ct);
                logger.LogDebug(
                    "Профиль пользователя {UserId} (TelegramId={TelegramId}) обновлён: DisplayName={DisplayName}, TgUsername={TgUsername}",
                    existing.Id, telegramId, existing.DisplayName, existing.TgUsername);
            }

            return existing.Id;
        }

        // Первый вход через Telegram: хэндл зеркалится в TgUsername всегда, а в видимый
        // (уникальный) Username — только если он валиден по формату и ещё не занят другим
        // аккаунтом. Коллизия оставляет Username пустым — пользователь не получает чужой
        // хэндл и не блокирует создание своего аккаунта; назначить свой Username позже
        // (профиль-эндпоинт) — отдельная задача, вне этой цепочки.
        string? appUsername = null;
        if (!string.IsNullOrWhiteSpace(username))
        {
            var normalized = UsernameRules.Normalize(username);
            if (UsernameRules.IsValid(normalized) && !await db.Users.AnyAsync(u => u.Username == normalized, ct))
                appUsername = normalized;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            TelegramId = telegramId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"User {telegramId}" : displayName,
            Username = appUsername,
            TgUsername = username,
            CreatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Создан новый пользователь {UserId} (TelegramId={TelegramId}, DisplayName={DisplayName})",
                user.Id, telegramId, user.DisplayName);
        }
        catch (DbUpdateException ex)
        {
            // Гонка: другой запрос того же TelegramId уже создал пользователя
            // между нашим SELECT и INSERT (UNIQUE-индекс на TelegramId это поймал).
            logger.LogDebug(ex, "Гонка при создании пользователя TelegramId={TelegramId}, повторное чтение", telegramId);
            db.Entry(user).State = EntityState.Detached;
            var raceId = await db.Users.AsNoTracking()
                .Where(u => u.TelegramId == telegramId)
                .Select(u => u.Id)
                .FirstOrDefaultAsync(ct);
            if (raceId != Guid.Empty) return raceId;

            // Ничего не нашлось по TelegramId — значит конфликт был не по нему, а по
            // Username (крайне узкое окно между предварительной проверкой и вставкой:
            // тот же нормализованный хэндл только что заняли через PWA-регистрацию).
            // Наш собственный insert так и не прошёл — повторяем без app Username.
            logger.LogDebug(
                "Гонка при создании пользователя TelegramId={TelegramId} была не по TelegramId — повтор без Username", telegramId);
            user.Username = null;
            db.Entry(user).State = EntityState.Added;
            await db.SaveChangesAsync(ct);
            return user.Id;
        }

        return user.Id;
    }
}
