using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Ежедневная фоновая джоба (Hangfire recurring job, этап 3 п.10 брифа): сканирует сроки
/// годности лекарств и приближающиеся дни рождения, создаёт оповещения получателям — активным
/// членам соответствующей семьи, идемпотентно (UNIQUE по DedupKey), и отправляет ещё не
/// отправленные через INotificationSender.
/// </summary>
public class ReminderScanJob(
    AppDbContext db,
    INotificationSender sender,
    IOptions<NotificationOptions> options)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await ScanMedicationsAsync(today, ct);
        await ScanBirthdaysAsync(today, ct);
        await SendPendingAsync(ct);
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

        foreach (var med in medications)
        {
            var isExpired = med.ExpiryDate < today;
            var type = isExpired ? NotificationType.MedicationExpired : NotificationType.MedicationExpiringSoon;
            var dedupPrefix = isExpired ? "med-expired" : "med-exp";

            var (title, body) = isExpired
                ? ($"Срок годности истёк: {med.Name}", $"Лекарство «{med.Name}» просрочено с {med.ExpiryDate:dd.MM.yyyy}.")
                : ($"Истекает срок годности: {med.Name}", $"Лекарство «{med.Name}» истекает {med.ExpiryDate:dd.MM.yyyy}.");

            foreach (var userId in await GetActiveFamilyMemberIdsAsync(med.FamilyId, ct))
            {
                var dedupKey = $"{dedupPrefix}:{med.Id}:{userId}";
                await AddNotificationIfNewAsync(userId, med.FamilyId, type, title, body, med.Id, dedupKey, ct);
            }
        }
    }

    private async Task ScanBirthdaysAsync(DateOnly today, CancellationToken ct)
    {
        var birthdays = await db.Birthdays.AsNoTracking()
            .Select(b => new { b.Id, b.FamilyId, b.PersonName, b.Date })
            .ToListAsync(ct);

        foreach (var bday in birthdays)
        {
            var nextOccurrence = NextOccurrence(bday.Date, today);
            var daysUntil = nextOccurrence.DayNumber - today.DayNumber;
            if (daysUntil < 0 || daysUntil > options.Value.BirthdayWarningDays) continue;

            var title = $"Скоро день рождения: {bday.PersonName}";
            var body = daysUntil == 0
                ? $"У {bday.PersonName} день рождения сегодня!"
                : $"У {bday.PersonName} день рождения {nextOccurrence:dd.MM.yyyy} (через {daysUntil} дн.).";

            foreach (var userId in await GetActiveFamilyMemberIdsAsync(bday.FamilyId, ct))
            {
                // Год — год наступающего повтора, иначе ежегодное ДР не получило бы новое
                // оповещение в следующем году (DedupKey уже был бы занят прошлогодней записью).
                var dedupKey = $"bday:{bday.Id}:{userId}:{nextOccurrence.Year}";
                await AddNotificationIfNewAsync(userId, bday.FamilyId, NotificationType.BirthdayUpcoming, title, body, bday.Id, dedupKey, ct);
            }
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

    private async Task<List<Guid>> GetActiveFamilyMemberIdsAsync(Guid familyId, CancellationToken ct) =>
        await db.FamilyMembers.AsNoTracking()
            .Where(fm => fm.FamilyId == familyId && fm.Status == MemberStatus.Active)
            .Select(fm => fm.UserId)
            .ToListAsync(ct);

    private async Task AddNotificationIfNewAsync(
        Guid userId, Guid familyId, NotificationType type, string title, string body,
        Guid relatedEntityId, string dedupKey, CancellationToken ct)
    {
        if (await db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) return;

        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            Type = type,
            Title = title,
            Body = body,
            RelatedEntityId = relatedEntityId,
            DedupKey = dedupKey,
            CreatedAt = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Гонка двух прогонов джобы вставила тот же DedupKey раньше нас — UNIQUE-индекс
            // это и есть страховка идемпотентности, поэтому просто откатываем локальный трекинг.
            db.ChangeTracker.Clear();
        }
    }

    private async Task SendPendingAsync(CancellationToken ct)
    {
        // Подхватывает и неотправленные с прошлых прогонов (например, если sender упал) —
        // не только созданные в этом вызове.
        var pending = await db.Notifications.Where(n => n.SentAt == null).ToListAsync(ct);
        if (pending.Count == 0) return;

        foreach (var notification in pending)
        {
            await sender.SendAsync(notification, ct);
            notification.SentAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }
}
