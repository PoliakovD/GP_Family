using System.Net;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebPush;
// Пакет WebPush тоже объявляет тип PushSubscription — алиас на доменную сущность, чтобы не
// писать WebPush.PushSubscription/FamilyHub.Domain.Entities.PushSubscription полным именем везде.
using DomainPushSubscription = FamilyHub.Domain.Entities.PushSubscription;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Реальная доставка через Web Push (редизайн навигации, ADR-0004) — покрывает PWA-пользователей,
/// которых TelegramNotificationSender не видит вовсе (нет TelegramId). Egress на иностранные
/// push-релеи (FCM/Mozilla/Apple) — осознанное исключение из ADR-0001, см. ADR-0004.
/// </summary>
/// <remarks>
/// Payload — ТОЛЬКО обобщённый текст, никогда <see cref="Notification.Title"/>/<see cref="Notification.Body"/>
/// (ADR-0002 отмечает их как потенциально содержащие имена — прямо противоречит "обобщённый payload"
/// из ADR-0004). Реальный контент — за уже аутентифицированным /api/notifications, открывается
/// кликом по системному уведомлению. JSON-форма <c>{"notification":{...}}</c> — формат, который
/// сгенерированный Angular ngsw-worker.js понимает "из коробки" (свой код service worker не нужен).
/// </remarks>
public class WebPushNotificationSender(
    IWebPushClient client,
    AppDbContext db,
    ILogger<WebPushNotificationSender> logger) : INotificationSender
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public NotificationChannel Channel => NotificationChannel.WebPush;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var subscriptions = await db.Set<DomainPushSubscription>()
            .Where(s => s.UserId == notification.UserId)
            .ToListAsync(ct);

        if (subscriptions.Count == 0) return;

        var payload = BuildPayload();
        var expired = new List<DomainPushSubscription>();

        foreach (var subscription in subscriptions)
        {
            try
            {
                var pushSubscription = new WebPush.PushSubscription(
                    subscription.Endpoint, subscription.P256dh, subscription.Auth);
                await client.SendNotificationAsync(pushSubscription, payload, cancellationToken: ct);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                // Push-релей сообщил, что подписка больше не существует (устройство отписалось/
                // сбросило хранилище) — чистим, иначе будем биться в неё при каждом уведомлении.
                logger.LogDebug(
                    "Push-подписка {SubscriptionId} протухла ({Status}) — удаляем.",
                    subscription.Id, ex.StatusCode);
                expired.Add(subscription);
            }
            catch (Exception ex)
            {
                // Сбой одной подписки не должен прерывать остальные — тот же принцип изоляции,
                // что в TelegramNotificationSender.SendAsync.
                logger.LogError(
                    ex, "Не удалось отправить push-уведомление {NotificationId} подписке {SubscriptionId}.",
                    notification.Id, subscription.Id);
            }
        }

        if (expired.Count > 0)
        {
            db.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }
    }

    private static string BuildPayload() => JsonSerializer.Serialize(
        new PushPayload(new PushNotificationPayload(
            "FamilyHub",
            "Новое уведомление",
            "/icons/icon-192.png",
            new PushActionData(new PushClickAction(new PushNavigateOperation("navigate", "/notifications"))))),
        PayloadJsonOptions);

    // Формат, ожидаемый Angular ngsw-worker.js (camelCase через PayloadJsonOptions):
    // {"notification":{"title":..,"body":..,"icon":..,"data":{"onActionClick":{"default":{"operation":"navigate","url":".."}}}}}
    private sealed record PushPayload(PushNotificationPayload Notification);
    private sealed record PushNotificationPayload(string Title, string Body, string Icon, PushActionData Data);
    private sealed record PushActionData(PushClickAction OnActionClick);
    private sealed record PushClickAction(PushNavigateOperation Default);
    private sealed record PushNavigateOperation(string Operation, string Url);
}
