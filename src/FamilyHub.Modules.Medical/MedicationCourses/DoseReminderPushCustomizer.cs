using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>
/// Push-напоминание о приёме с кнопками (ADR-0015). Текст обобщённый — «Время принять лекарство · 14:00»,
/// без названия препарата и имён (ADR-0004: push уходит через сторонние релеи). Кнопки «Принял»,
/// «Отложить 10/30 минут», «Пропустить» вызывают в фоне публичный эндпоинт с одноразовым токеном; клик по
/// самому уведомлению открывает приём в приложении. Браузеры показывают не больше двух кнопок, iOS — ни
/// одной, поэтому порядок важен: сначала «Принял» и «Отложить на 10 минут». Тихие часы получателя
/// делают уведомление беззвучным. Если приём уже закрыт, push не отправляется.
/// </summary>
public class DoseReminderPushCustomizer(AppDbContext db, DoseActionTokenService tokens) : IPushPayloadCustomizer
{
    public bool CanHandle(NotificationType type) => type == NotificationType.MedicationDoseDue;

    public async Task<PushCustomization?> BuildAsync(Notification notification, CancellationToken ct = default)
    {
        var dose = await db.MedicationDoses.AsNoTracking().FirstOrDefaultAsync(d => d.Id == notification.RelatedEntityId, ct);
        if (dose is null || dose.Status is not (DoseStatus.Pending or DoseStatus.Snoozed) || dose.ScheduledAt is null)
            return null;

        var user = await db.Users.AsNoTracking().Where(u => u.Id == notification.UserId)
            .Select(u => new { u.TimeZoneId, u.QuietHoursFrom, u.QuietHoursTo }).FirstOrDefaultAsync(ct);
        var tz = TimeZones.Resolve(user?.TimeZoneId);
        var now = DateTime.UtcNow;
        var time = TimeZoneInfo.ConvertTimeFromUtc(dose.ScheduledAt.Value, tz).ToString("H:mm");

        var token = await tokens.IssueAsync(dose, notification.UserId, ct);
        string Url(string action) => $"/api/public/dose-actions/{token}?a={action}";

        return new PushCustomization(
            Body: $"Время принять лекарство · {time}",
            Tag: $"dose-{dose.Id}",
            Renotify: true,
            RequireInteraction: true,
            Silent: QuietHours.IsQuiet(user?.QuietHoursFrom, user?.QuietHoursTo, now, tz),
            Actions:
            [
                new PushActionButton(ActionTaken, "Принял"),
                new PushActionButton(ActionSnooze10, "Отложить на 10 минут"),
                new PushActionButton(ActionSnooze30, "Отложить на 30 минут"),
                new PushActionButton(ActionSkip, "Пропустить"),
            ],
            DefaultUrl: $"/health/intake/dose/{dose.Id}",
            SendRequestUrls: new Dictionary<string, string>
            {
                [ActionTaken] = Url(ActionTaken),
                [ActionSnooze10] = Url(ActionSnooze10),
                [ActionSnooze30] = Url(ActionSnooze30),
                [ActionSkip] = Url(ActionSkip),
            });
    }

    public const string ActionTaken = "taken";
    public const string ActionSnooze10 = "snooze10";
    public const string ActionSnooze30 = "snooze30";
    public const string ActionSkip = "skip";

    /// <summary>Значение параметра <c>a</c> → действие; null — неизвестное.</summary>
    public static DoseAction? ParseAction(string? value) => value switch
    {
        ActionTaken => DoseAction.Taken,
        ActionSnooze10 => DoseAction.Snooze10,
        ActionSnooze30 => DoseAction.Snooze30,
        ActionSkip => DoseAction.Skip,
        _ => null,
    };
}
