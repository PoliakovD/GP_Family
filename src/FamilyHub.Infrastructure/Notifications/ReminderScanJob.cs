using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Ежедневная фоновая джоба (Hangfire recurring job, этап 3 п.10 брифа): сканирует сроки
/// годности лекарств и приближающиеся дни рождения. С этапа 1 плана сама оповещений не
/// создаёт — публикует MedicationExpiringEvent/BirthdayApproachingEvent в outbox, фан-аут
/// по получателям делает Notifications-хендлер (идемпотентно, UNIQUE по DedupKey).
/// SendPendingAsync остаётся ретрай-свипом недоставленных оповещений.
/// </summary>
public class ReminderScanJob(
    AppDbContext db,
    IOutboxWriter outbox,
    NotificationSendingService notifications,
    IOptions<NotificationOptions> options,
    ILogger<ReminderScanJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        logger.LogInformation("Запуск ReminderScanJob за {Today}", today);

        await ScanMedicationsAsync(today, ct);
        await ScanBirthdaysAsync(today, ct);
        await db.SaveChangesAsync(ct); // фиксация поставленных в outbox событий
        await SendPendingAsync(ct);

        logger.LogInformation("ReminderScanJob завершён");
    }

    private async Task ScanMedicationsAsync(DateOnly today, CancellationToken ct)
    {
        var warningCutoff = today.AddDays(options.Value.ExpiryWarningDays);

        // Запрос покрывает и уже просроченные (ExpiryDate < today), и приближающиеся
        // (today <= ExpiryDate <= warningCutoff) — различаем тип ниже по дате.
        var medications = await db.Medications.AsNoTracking()
            .Where(m => m.ExpiryDate != null && m.ExpiryDate <= warningCutoff)
            .Select(m => new { m.Id, m.FamilyId, m.Name, ExpiryDate = m.ExpiryDate!.Value })
            .ToListAsync(ct);

        logger.LogDebug("Скан медикаментов: {Count} кандидатов до {WarningCutoff}", medications.Count, warningCutoff);

        foreach (var med in medications)
        {
            var isExpired = med.ExpiryDate < today;
            var type = isExpired ? NotificationType.MedicationExpired : NotificationType.MedicationExpiringSoon;

            // Префильтр от спама событий при ежедневном рескане: если оповещения по этому
            // поводу уже созданы, событие не публикуем. Переход expiring→expired меняет Type
            // и потому породит новое событие. Гонку двух прогонов страхует per-user DedupKey
            // в хендлере — дубль события не создаст дублей оповещений.
            if (await db.Notifications.AnyAsync(n => n.RelatedEntityId == med.Id && n.Type == type, ct))
                continue;

            outbox.Enqueue(new MedicationExpiringEvent(med.Id, med.FamilyId, med.Name, med.ExpiryDate, isExpired));
        }
    }

    private async Task ScanBirthdaysAsync(DateOnly today, CancellationToken ct)
    {
        var birthdays = await db.Birthdays.AsNoTracking()
            .Select(b => new { b.Id, b.FamilyId, b.PersonName, b.Date })
            .ToListAsync(ct);

        logger.LogDebug("Скан дней рождения: {Count} записей", birthdays.Count);

        foreach (var bday in birthdays)
        {
            var nextOccurrence = NextOccurrence(bday.Date, today);
            var daysUntil = nextOccurrence.DayNumber - today.DayNumber;
            if (daysUntil < 0 || daysUntil > options.Value.BirthdayWarningDays) continue;

            // Год наступающего повтора в суффиксе ключа — иначе ежегодное ДР не получило бы
            // новое оповещение в следующем году. Префильтр — по тому же суффиксу.
            var yearSuffix = $":{nextOccurrence.Year}";
            if (await db.Notifications.AnyAsync(
                    n => n.RelatedEntityId == bday.Id
                        && n.Type == NotificationType.BirthdayUpcoming
                        && n.DedupKey.EndsWith(yearSuffix), ct))
                continue;

            outbox.Enqueue(new BirthdayApproachingEvent(bday.Id, bday.FamilyId, bday.PersonName, nextOccurrence, daysUntil));
        }
    }

    /// <summary>Ближайшая (в этом или следующем году) календарная дата дня рождения от today.</summary>
    private static DateOnly NextOccurrence(DateOnly birthDate, DateOnly today)
    {
        var candidate = SafeDate(today.Year, birthDate.Month, birthDate.Day);
        return candidate < today ? SafeDate(today.Year + 1, birthDate.Month, birthDate.Day) : candidate;
    }

    /// <summary>29 февраля в невисокосный год переносим на 28 февраля, а не падаем с исключением.</summary>
    private static DateOnly SafeDate(int year, int month, int day) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));

    private async Task SendPendingAsync(CancellationToken ct)
    {
        // Ретрай-свип: подхватывает оповещения, чья отправка не удалась хендлерам
        // (например, sender упал) — не только созданные по событиям этого прогона.
        var pending = await db.Notifications.Where(n => n.SentAt == null).ToListAsync(ct);
        if (pending.Count == 0) return;

        logger.LogDebug("Отправка {Count} неотправленных оповещений", pending.Count);

        foreach (var notification in pending)
            await notifications.TrySendAsync(notification, ct);

        await db.SaveChangesAsync(ct);
    }
}
