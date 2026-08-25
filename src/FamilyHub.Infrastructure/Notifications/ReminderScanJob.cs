using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Notifications;

/// <summary>
/// Ежедневная фоновая джоба (Hangfire recurring job, этап 3 п.10 брифа): сканирует сроки
/// годности лекарств и приближающиеся дни рождения. С этапа 1 плана сама оповещений не
/// создаёт — публикует MedicationExpiringEvent/BirthdayApproachingEvent через шину (ADR-0006),
/// фан-аут по получателям делает Notifications-потребитель (идемпотентно, UNIQUE по DedupKey).
/// SendPendingAsync остаётся ретрай-свипом недоставленных оповещений.
///
/// Дни рождения (identity rework) — три независимых источника, каждый публикует тот же тип
/// события с разным BirthdaySubjectKind: ручные записи Birthday (Manual), активные члены семьи
/// с заполненным User.BirthDate (Member), подопечные с заполненным FamilyDependent.BirthDate
/// (Dependent). Общая логика окна/дедупа/переноса 29 февраля вынесена в TryPublishBirthdayAsync.
/// </summary>
public class ReminderScanJob(
    AppDbContext db,
    IDomainEventPublisher publisher,
    NotificationSendingService notifications,
    IOptions<NotificationOptions> options,
    ILogger<ReminderScanJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        logger.LogInformation("Запуск ReminderScanJob за {Today}", today);

        await ScanMedicationsAsync(today, ct);
        await ScanManualBirthdaysAsync(today, ct);
        await ScanMemberBirthdaysAsync(today, ct);
        await ScanDependentBirthdaysAsync(today, ct);
        await db.SaveChangesAsync(ct); // фиксация поставленных в outbox строк шины
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

            await publisher.PublishAsync(new MedicationExpiringEvent(med.Id, med.FamilyId, med.Name, med.ExpiryDate, isExpired), ct);
        }
    }

    private async Task ScanManualBirthdaysAsync(DateOnly today, CancellationToken ct)
    {
        var birthdays = await db.Birthdays.AsNoTracking()
            .Select(b => new { b.Id, b.FamilyId, b.PersonName, b.Date })
            .ToListAsync(ct);

        logger.LogDebug("Скан ручных дней рождения: {Count} записей", birthdays.Count);

        foreach (var bday in birthdays)
        {
            await TryPublishBirthdayAsync(
                BirthdaySubjectKind.Manual, bday.Id, bday.FamilyId, bday.PersonName, bday.Date,
                today, subjectUserId: null, ct);
        }
    }

    /// <summary>
    /// Активные члены семьи с заполненным профилем ДР — одно событие на КАЖДУЮ пару
    /// (пользователь, семья): человек в трёх семьях получает три отдельных напоминания
    /// (получатели в каждой семье разные). SubjectUserId = сам именинник — потребитель
    /// (BirthdayApproachingNotificationConsumer) исключает его из получателей.
    /// </summary>
    private async Task ScanMemberBirthdaysAsync(DateOnly today, CancellationToken ct)
    {
        var members = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.Status == MemberStatus.Active && m.User.BirthDate != null)
            .Select(m => new
            {
                m.UserId, m.FamilyId, m.User.LastName, m.User.FirstName, m.User.MiddleName,
                BirthDate = m.User.BirthDate!.Value,
            })
            .ToListAsync(ct);

        logger.LogDebug("Скан дней рождения участников: {Count} пар (участник, семья)", members.Count);

        foreach (var m in members)
        {
            // BirthDate заполняется только вместе с LastName/FirstName (PersonName.IsCompleteProfile
            // — единственный путь записи, см. PwaAuthService/ProfileService) — FormatOrDefault
            // остаётся лишь страховкой на случай рассинхронизации данных.
            var personName = PersonName.FormatOrDefault(
                m.LastName, m.FirstName, m.MiddleName, PersonNameStyle.Full, fallback: "Участник семьи");
            await TryPublishBirthdayAsync(
                BirthdaySubjectKind.Member, m.UserId, m.FamilyId, personName, m.BirthDate,
                today, subjectUserId: m.UserId, ct);
        }
    }

    private async Task ScanDependentBirthdaysAsync(DateOnly today, CancellationToken ct)
    {
        // FirstName/LastName зашифрованы — материализуем сущности и форматируем в памяти (тот же
        // приём, что FamilyDependentService.GetForFamilyAsync); фильтр BirthDate != null остаётся
        // в SQL (не шифруется, см. ADR-0002).
        var dependents = await db.FamilyDependents.AsNoTracking()
            .Where(d => d.BirthDate != null)
            .ToListAsync(ct);

        logger.LogDebug("Скан дней рождения подопечных: {Count} записей", dependents.Count);

        foreach (var d in dependents)
        {
            // Питомец — кличка (FirstName), без фамилии/отчества. FirstName — тоже фолбэк для
            // человека без заполненной фамилии (легитимно для FamilyDependent, в отличие от User).
            var personName = d.IsPet
                ? d.FirstName
                : PersonName.FormatOrDefault(d.LastName, d.FirstName, d.MiddleName, PersonNameStyle.Full, fallback: d.FirstName);
            await TryPublishBirthdayAsync(
                BirthdaySubjectKind.Dependent, d.Id, d.FamilyId, personName, d.BirthDate!.Value,
                today, subjectUserId: null, ct);
        }
    }

    /// <summary>
    /// Общая логика для всех трёх источников: окно предупреждения, перенос 29 февраля,
    /// префильтр от повторной публикации при ежегодном рескане (год наступающего повтора —
    /// в суффиксе DedupKey, иначе следующее наступление того же ДР не получило бы новое
    /// оповещение). FamilyId — часть префильтра начиная с identity rework: один и тот же
    /// SubjectId (участник) теперь легитимно повторяется в нескольких семьях одновременно.
    /// </summary>
    private async Task TryPublishBirthdayAsync(
        BirthdaySubjectKind kind, Guid subjectId, Guid familyId, string personName, DateOnly birthDate,
        DateOnly today, Guid? subjectUserId, CancellationToken ct)
    {
        var nextOccurrence = NextOccurrence(birthDate, today);
        var daysUntil = nextOccurrence.DayNumber - today.DayNumber;
        if (daysUntil < 0 || daysUntil > options.Value.BirthdayWarningDays) return;

        var yearSuffix = $":{nextOccurrence.Year}";
        if (await db.Notifications.AnyAsync(
                n => n.RelatedEntityId == subjectId && n.FamilyId == familyId
                    && n.Type == NotificationType.BirthdayUpcoming
                    && n.DedupKey.EndsWith(yearSuffix), ct))
            return;

        await publisher.PublishAsync(
            new BirthdayApproachingEvent(kind, subjectId, familyId, personName, nextOccurrence, daysUntil, subjectUserId), ct);
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
        // Ограничен по возрасту (RetrySweepMaxAgeDays) — без верхней границы при затяжном сбое
        // (например, истёкший VAPID/Bot Token) строки копились бы и пытались отправиться каждый
        // день бесконечно (см. аудит module-review-2026-08-02/06-notifications-push-bot-outbox.md,
        // находка 3).
        var cutoff = DateTime.UtcNow.AddDays(-options.Value.RetrySweepMaxAgeDays);
        var pending = await db.Notifications.Where(n => n.SentAt == null && n.CreatedAt >= cutoff).ToListAsync(ct);

        // Не просто молча исключаем устаревшие строки из свипа — иначе затяжной сбой канала
        // остался бы незамеченным навсегда. Считаем отдельным запросом (не грузим сами записи).
        var staleCount = await db.Notifications.CountAsync(n => n.SentAt == null && n.CreatedAt < cutoff, ct);
        if (staleCount > 0)
        {
            logger.LogWarning(
                "{StaleCount} недоставленных оповещений старше {MaxAgeDays} дн. исключены из ретрай-свипа — " +
                "возможен затяжной сбой канала доставки (Telegram Bot Token/Web Push VAPID)",
                staleCount, options.Value.RetrySweepMaxAgeDays);
        }

        if (pending.Count == 0) return;

        logger.LogDebug("Отправка {Count} неотправленных оповещений", pending.Count);

        foreach (var notification in pending)
            await notifications.TrySendAsync(notification, ct);

        await db.SaveChangesAsync(ct);
    }
}
