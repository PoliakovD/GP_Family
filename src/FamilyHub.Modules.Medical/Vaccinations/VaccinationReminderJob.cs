using FamilyHub.Domain.Enums;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>
/// Раз в день (Hangfire recurring, ADR-0016): по каждому человеку с известной датой рождения считает
/// график и шлёт напоминания — «за 2 недели до срока» и «срок прошёл, можно сделать» — ровно так же,
/// как ADR-0015 описывает для приёма лекарств («напоминаем за 2 недели до срока и если срок прошёл»).
/// Каждое из двух уведомлений — одноразовое: дедуп-ключ не зависит от даты прогона, только от
/// (человек, серия, доза), поэтому окно в несколько дней внутри статуса DueSoon/CanDo не спамит, а
/// пропуск одного прогона (простой джобы) не теряет напоминание — оно уйдёт на следующем тике, пока
/// статус ещё держится. Наблюдатели — та же таблица MedicationWatchers, что у курсов приёма
/// (см. SubjectScopeService/ADR-0015): в проекте не заводили отдельной таблицы «кто следит за
/// прививками», семантика «следить за человеком» уже общая для курсов и прививок.
///
/// Тексты — без названия инфекции/вакцины (ADR-0004/ADR-0016, та же причина, что у MedicationDoseDue:
/// таблица Notifications не шифруется, Telegram пересылает текст дословно).
/// </summary>
[DisableConcurrentExecution(600)]
[AutomaticRetry(Attempts = 0)]
public class VaccinationReminderJob(
    AppDbContext db, NotificationSendingService notifications, ILogger<VaccinationReminderJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var users = await db.Users.AsNoTracking().Where(u => u.BirthDate != null)
            .Select(u => new { u.Id, BirthDate = u.BirthDate!.Value, u.FirstName, u.Username }).ToListAsync(ct);
        foreach (var u in users)
        {
            try
            {
                await ScanSubjectAsync(VaccinationSubjects.UserKind, u.Id, null, u.BirthDate, FirstNonEmpty(u.FirstName, u.Username), today, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Сбой сканирования прививок пользователя {UserId}", u.Id);
                db.ChangeTracker.Clear();
            }
        }

        var dependents = await db.FamilyDependents.AsNoTracking().Where(d => !d.IsPet && d.BirthDate != null).ToListAsync(ct);
        foreach (var d in dependents)
        {
            try
            {
                await ScanSubjectAsync(VaccinationSubjects.DependentKind, d.Id, d.FamilyId, d.BirthDate!.Value, FirstNonEmpty(d.FirstName), today, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Сбой сканирования прививок подопечного {DependentId}", d.Id);
                db.ChangeTracker.Clear();
            }
        }

        await CheckWellbeingAsync(ct);
    }

    private async Task ScanSubjectAsync(
        string kind, Guid subjectId, Guid? familyId, DateOnly birth, string name, DateOnly today, CancellationToken ct)
    {
        var query = kind == VaccinationSubjects.UserKind
            ? db.Vaccinations.AsNoTracking().Where(v => v.SubjectUserId == subjectId)
            : db.Vaccinations.AsNoTracking().Where(v => v.FamilyDependentId == subjectId);
        var facts = (await query.Where(v => v.SeriesCode != null).ToListAsync(ct))
            .Select(v => new VaccinationFact(v.Id, v.SeriesCode!, v.DoseIndex, v.Kind, v.Date, v.DatePrecision))
            .ToList();

        var items = VaccinationScheduleCalculator.Calculate(birth, facts, today);
        List<Guid>? recipients = null;

        foreach (var item in items)
        {
            (NotificationType?, string?, string?, string?) row = item.Status switch
            {
                VaccinationStatus.DueSoon => (NotificationType.VaccinationDue, "Скоро прививка",
                    "Пора запланировать прививку по календарю", $"{name}: пора запланировать прививку по календарю"),
                VaccinationStatus.CanDo => (NotificationType.VaccinationOverdue, "Можно сделать прививку",
                    "Есть прививка, срок которой прошёл — это не страшно", $"{name}: есть прививка, срок которой прошёл"),
                _ => (null, null, null, null),
            };
            var (type, title, ownBody, othersBody) = row;
            if (type is null) continue;

            recipients ??= await RecipientsForAsync(kind, subjectId, familyId, ct);
            if (recipients.Count == 0) continue;

            var dedupBase = $"vacc-{(type == NotificationType.VaccinationDue ? "due" : "overdue")}:{kind}:{subjectId}:{item.SeriesCode}:{item.DoseIndex}";
            var relatedKind = kind == VaccinationSubjects.UserKind ? NotificationRelatedKind.VaccinationPersonUser : NotificationRelatedKind.VaccinationPersonDependent;

            foreach (var userId in recipients)
            {
                var isSubject = kind == VaccinationSubjects.UserKind && userId == subjectId;
                await notifications.NotifyAsync(
                    [userId], familyId, type.Value, title!, isSubject ? ownBody! : othersBody!, subjectId,
                    _ => $"{dedupBase}:{userId}", ct, relatedKind);
            }
        }
    }

    /// <summary>«Напомнить о самочувствии через 7 дней» — только для своих прививок (запрашивается в
    /// шторке после сохранения); одноразово, WellbeingCheckSent — тот же приём, что LowStockNotifiedAt
    /// у MedicationCourse.</summary>
    private async Task CheckWellbeingAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.Vaccinations
            .Where(v => v.WellbeingCheckAt != null && v.WellbeingCheckAt <= now && !v.WellbeingCheckSent && v.SubjectUserId != null)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        foreach (var v in due)
        {
            await notifications.NotifyAsync(
                [v.SubjectUserId!.Value], null, NotificationType.VaccinationWellbeingCheck,
                "Как самочувствие?", "Как самочувствие после прививки? Запись в дневнике поможет не забыть детали.",
                v.Id, _ => $"vacc-wellbeing:{v.Id}", ct, null);
            v.WellbeingCheckSent = true;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Guid>> RecipientsForAsync(string kind, Guid subjectId, Guid? familyId, CancellationToken ct)
    {
        if (kind == VaccinationSubjects.UserKind)
        {
            var result = new List<Guid> { subjectId };
            var watcherIds = await db.MedicationWatchers.AsNoTracking()
                .Where(w => w.SubjectUserId == subjectId && w.ReceiveReminders)
                .Select(w => w.WatcherUserId).ToListAsync(ct);
            if (watcherIds.Count > 0)
            {
                var subjectFamilies = db.FamilyMembers.Where(m => m.UserId == subjectId && m.Status == MemberStatus.Active).Select(m => m.FamilyId);
                var sharing = await db.FamilyMembers.AsNoTracking()
                    .Where(m => m.Status == MemberStatus.Active && watcherIds.Contains(m.UserId) && subjectFamilies.Contains(m.FamilyId))
                    .Select(m => m.UserId).Distinct().ToListAsync(ct);
                result.AddRange(sharing);
            }
            return result.Distinct().ToList();
        }

        if (familyId is not { } fid) return [];
        var watchers = await db.MedicationWatchers.AsNoTracking()
            .Where(w => w.FamilyDependentId == subjectId && w.ReceiveReminders)
            .Select(w => w.WatcherUserId).ToListAsync(ct);
        if (watchers.Count == 0) return [];
        return await db.FamilyMembers.AsNoTracking()
            .Where(m => m.FamilyId == fid && m.Status == MemberStatus.Active && watchers.Contains(m.UserId))
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "Родственник";
}
