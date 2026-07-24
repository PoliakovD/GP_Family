using System.Security.Cryptography;
using System.Text;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Push;

/// <summary>
/// Управление подписками Web Push (редизайн навигации, ADR-0004). Endpoint шифрован at-rest
/// (ADR-0002) — все lookup/upsert идут по EndpointHash (SHA-256, тот же приём, что CodeHash в
/// TelegramLinkCode/EmailVerificationCode), саму зашифрованную колонку не фильтруем.
/// </summary>
public class PushSubscriptionService(
    AppDbContext db, IOptions<WebPushOptions> webPushOptions, ILogger<PushSubscriptionService> logger)
{
    /// <summary>null — Web Push не настроен на бэкенде (нет VAPID-ключей); фронт скрывает тумблер.</summary>
    public string? GetVapidPublicKey() =>
        webPushOptions.Value.IsConfigured ? webPushOptions.Value.VapidPublicKey : null;

    /// <summary>
    /// Upsert по EndpointHash: то же устройство/браузер переподписывается (напр. другим
    /// пользователем на общем устройстве) — переносим владение и обновляем ключи, а не плодим дубли.
    /// </summary>
    public async Task SubscribeAsync(
        Guid userId, string endpoint, string p256dh, string auth, CancellationToken ct = default)
    {
        var hash = HashEndpoint(endpoint);
        var now = DateTime.UtcNow;

        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.EndpointHash == hash, ct);
        if (existing is not null)
        {
            existing.UserId = userId;
            existing.Endpoint = endpoint;
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.LastUsedAt = now;
        }
        else
        {
            db.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EndpointHash = hash,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                CreatedAt = now,
                LastUsedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Push-подписка сохранена для пользователя {UserId}", userId);
    }

    /// <summary>Отписка — только своя (по UserId), даже если тот же endpoint теперь принадлежит другому пользователю.</summary>
    public async Task<bool> UnsubscribeAsync(Guid userId, string endpoint, CancellationToken ct = default)
    {
        var hash = HashEndpoint(endpoint);
        var subscription = await db.PushSubscriptions.FirstOrDefaultAsync(
            s => s.EndpointHash == hash && s.UserId == userId, ct);
        if (subscription is null)
        {
            logger.LogDebug("Отписка: подписка не найдена у пользователя {UserId}", userId);
            return false;
        }

        db.PushSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Push-подписка удалена пользователем {UserId}", userId);
        return true;
    }

    private static string HashEndpoint(string endpoint) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
}
