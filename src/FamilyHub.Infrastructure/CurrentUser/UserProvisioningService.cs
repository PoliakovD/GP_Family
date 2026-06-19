using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.CurrentUser;

public class UserProvisioningService(AppDbContext db) : IUserProvisioningService
{
    public async Task<Guid> GetOrCreateUserIdAsync(long telegramId, string? displayName, CancellationToken ct = default)
    {
        var existingId = await db.Users.AsNoTracking()
            .Where(u => u.TelegramId == telegramId)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId != Guid.Empty)
            return existingId;

        var user = new User
        {
            Id = Guid.NewGuid(),
            TelegramId = telegramId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? $"User {telegramId}" : displayName,
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
