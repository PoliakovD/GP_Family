using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>Кнопка в push-уведомлении (браузеры показывают не больше двух, iOS — ни одной).</summary>
public record PushActionButton(string Action, string Title);

/// <summary>
/// Что тип уведомления добавляет к обобщённому push-payload (ADR-0015, уточняет ADR-0004 п.2): текст
/// по-прежнему без имён и препаратов, но уведомление может нести тег (замена предыдущего), кнопки и
/// «тихий» режим. <see cref="SendRequestUrls"/> — действие кнопки → относительный URL, который
/// service worker вызовет в фоне (операция <c>sendRequest</c> Angular ngsw), не открывая приложение.
/// </summary>
public record PushCustomization(
    string? Body = null,
    string? Tag = null,
    bool Renotify = false,
    bool RequireInteraction = false,
    bool Silent = false,
    IReadOnlyList<PushActionButton>? Actions = null,
    string? DefaultUrl = null,
    IReadOnlyDictionary<string, string>? SendRequestUrls = null);

/// <summary>
/// Расширение payload'а Web Push для конкретного типа уведомления. Реализация из модуля (например,
/// Medical) подключается через DI, а сам WebPushNotificationSender остаётся в Infrastructure и не знает
/// о доменах модулей. Для типов без кастомизатора payload прежний — обобщённый, с переходом на /notifications.
/// </summary>
public interface IPushPayloadCustomizer
{
    bool CanHandle(NotificationType type);

    /// <summary>Дополнение к payload'у; <c>null</c> — уведомление больше не актуально (например, приём уже
    /// отмечен) и push отправлять не нужно.</summary>
    Task<PushCustomization?> BuildAsync(Notification notification, CancellationToken ct = default);
}
