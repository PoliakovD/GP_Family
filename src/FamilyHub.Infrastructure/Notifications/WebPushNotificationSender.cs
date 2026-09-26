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
    ILogger<WebPushNotificationSender> logger,
    IEnumerable<IPushPayloadCustomizer>? customizers = null) : INotificationSender
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public NotificationChannel Channel => NotificationChannel.WebPush;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var subscriptions = await db.Set<DomainPushSubscription>()
            .Where(s => s.UserId == notification.UserId)
            .ToListAsync(ct);

        if (subscriptions.Count == 0) return;

        // Тип уведомления может дополнить payload (кнопки, тег, тихий режим) — см. IPushPayloadCustomizer.
        // null от кастомизатора — уведомление уже не актуально, push не шлём.
        PushCustomization? customization = null;
        if (customizers?.FirstOrDefault(c => c.CanHandle(notification.Type)) is { } customizer)
        {
            customization = await customizer.BuildAsync(notification, ct);
            if (customization is null) return;
        }

        var payload = BuildPayload(customization);
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

    /// <summary>Обобщённый payload; кастомизация добавляет кнопки/тег/тишину и точечные действия клика.
    /// Формат <c>{"notification":{...}}</c> Angular ngsw-worker.js понимает сам: поля уведомления он
    /// передаёт в showNotification, а <c>data.onActionClick</c> — карта «кнопка → операция» (default —
    /// клик по самому уведомлению).</summary>
    public static string BuildPayload(PushCustomization? customization = null)
    {
        var onActionClick = new Dictionary<string, object>
        {
            ["default"] = new PushOperation(
                customization?.DefaultUrl is null ? "navigate" : "navigateLastFocusedOrOpen",
                customization?.DefaultUrl ?? "/notifications"),
        };
        foreach (var (action, url) in customization?.SendRequestUrls ?? new Dictionary<string, string>())
            onActionClick[action] = new PushOperation("sendRequest", url);

        var notification = new Dictionary<string, object?>
        {
            ["title"] = "FamilyHub",
            ["body"] = customization?.Body ?? "Новое уведомление",
            ["icon"] = "/icons/icon-192.png",
            ["data"] = new Dictionary<string, object> { ["onActionClick"] = onActionClick },
        };
        if (customization is not null)
        {
            if (customization.Tag is not null) notification["tag"] = customization.Tag;
            if (customization.Renotify) notification["renotify"] = true;
            if (customization.RequireInteraction) notification["requireInteraction"] = true;
            if (customization.Silent) notification["silent"] = true;
            if (customization.Actions is { Count: > 0 })
                notification["actions"] = customization.Actions.Select(a => new { action = a.Action, title = a.Title }).ToList();
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["notification"] = notification }, PayloadJsonOptions);
    }

    private sealed record PushOperation(string Operation, string Url);
}
