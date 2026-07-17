using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.CurrentUser;

public class UserProvisioningService(AppDbContext db) : IUserProvisioningService
{
    public async Task<Guid> GetOrCreateUserIdAsync(long telegramId, string? displayName, string? username = null, CancellationToken ct = default)
    {
        var existing = await db.Users
            .Where(u => u.TelegramId == telegramId)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            // Имя и username в Telegram могут меняться — держим их свежими на каждый логин.
            var changed = false;
            if (!string.IsNullOrWhiteSpace(displayName) && existing.DisplayName != displayName)
            {
                existing.DisplayName = displayName;
                changed = true;
            }
            if (existing.Username != username)
            {
                existing.Username = username;
                changed = true;
            }
            if (changed)
                await db.SaveChangesAsync(ct);

            return existing.Id;
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            TelegramId = telegramId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"User {telegramId}" : displayName,
            Username = username,
            CreatedAt = DateTime.UtcNow,
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка: другой запрос того же TelegramId уже создал пользователя
            // между нашим SELECT и INSERT (UNIQUE-индекс на TelegramId это поймал).
            db.Entry(user).State = EntityState.Detached;
            var raceId = await db.Users.AsNoTracking()
                .Where(u => u.TelegramId == telegramId)
                .Select(u => u.Id)
                .FirstAsync(ct);
            return raceId;
        }

        return user.Id;
    }
}
