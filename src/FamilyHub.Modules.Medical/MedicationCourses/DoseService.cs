using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.HealthNotes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.MedicationCourses;

/// <summary>
/// Действия над приёмами: «Принял» (со списанием из аптечки и записью в дневник), «Отложить»,
/// «Пропустить», «отметить приём по необходимости» и отмена отметки. Общий для приложения и кнопок
/// push (те приходят по одноразовому токену — см. DoseActionTokenService, права проверяются здесь так
/// же, как для сессии). Отметка, списание и запись дневника — одна транзакция.
/// </summary>
public class DoseService(
    AppDbContext db, MedicationCourseAccess courseAccess, MedkitStockService stock,
    HealthNoteService notes, ILogger<DoseService> logger)
{
    /// <summary>Отметить приём заранее можно не дальше чем за столько до планового времени.</summary>
    private static readonly TimeSpan EarlyTolerance = TimeSpan.FromHours(3);

    private static readonly TimeSpan FutureTakenTolerance = TimeSpan.FromMinutes(5);
    private static readonly DateTime EarliestAllowed = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Действие над плановым приёмом курса (строка создаётся, если её ещё нет).</summary>
    public async Task<(DoseResult Result, DoseDto? Item, string? Error)> ApplyAsync(
        Guid actorId, Guid courseId, DateTime scheduledAt, DoseAction action, DateTime? takenAt,
        CancellationToken ct = default)
    {
        var (result, dto, error) = await ApplyOnceAsync(actorId, courseId, scheduledAt, action, takenAt, ct);
        if (result == DoseResult.Success || result != DoseResult.Conflict || error != RaceMarker) return (result, dto, error);

        // Гонка с фоновой задачей: строка приёма появилась между проверкой и сохранением — повторяем один раз.
        return await ApplyOnceAsync(actorId, courseId, scheduledAt, action, takenAt, ct);
    }

    private const string RaceMarker = "race";

    private async Task<(DoseResult Result, DoseDto? Item, string? Error)> ApplyOnceAsync(
        Guid actorId, Guid courseId, DateTime scheduledAt, DoseAction action, DateTime? takenAt, CancellationToken ct)
    {
        var (course, access, _) = await courseAccess.LoadAsync(actorId, courseId, tracking: true, ct);
        if (course is null) return (DoseResult.NotFound, null, null);
        if (access != CourseAccess.Full) return (DoseResult.Forbidden, null, null);
        var gate = CheckActive(course);
        if (gate is not null) return (DoseResult.Conflict, null, gate);

        var scheduled = NormalizeUtc(scheduledAt);
        var dose = await db.MedicationDoses.FirstOrDefaultAsync(d => d.CourseId == courseId && d.ScheduledAt == scheduled, ct);
        if (dose is null)
        {
            var (created, error) = TryCreateDose(course, scheduled);
            if (created is null) return (DoseResult.Invalid, null, error);
            dose = created;
            db.MedicationDoses.Add(dose);
        }

        try
        {
            return await ExecuteAsync(actorId, course, dose, action, takenAt, ct);
        }
        catch (DbUpdateException ex) when (db.Entry(dose).State == EntityState.Added)
        {
            logger.LogInformation(ex, "Гонка при создании приёма курса {CourseId} на {At}: перечитываем", courseId, scheduled);
            db.ChangeTracker.Clear();
            return (DoseResult.Conflict, null, RaceMarker);
        }
    }

    /// <summary>Действие над существующим приёмом (кнопки push, отмена): приём известен по Id.</summary>
    public async Task<(DoseResult Result, DoseDto? Item, string? Error)> ApplyToDoseAsync(
        Guid actorId, Guid doseId, DoseAction action, CancellationToken ct = default)
    {
        var dose = await db.MedicationDoses.FirstOrDefaultAsync(d => d.Id == doseId, ct);
        if (dose is null) return (DoseResult.NotFound, null, null);

        var (course, access, _) = await courseAccess.LoadAsync(actorId, dose.CourseId, tracking: true, ct);
        if (course is null) return (DoseResult.NotFound, null, null);
        if (access != CourseAccess.Full) return (DoseResult.Forbidden, null, null);
        var gate = CheckActive(course);
        if (gate is not null) return (DoseResult.Conflict, null, gate);

        return await ExecuteAsync(actorId, course, dose, action, null, ct);
    }

    /// <summary>Отметить приём «по необходимости»; при превышении суточного лимита без
    /// <paramref name="force"/> — OverLimit (клиент спрашивает подтверждение).</summary>
    public async Task<(DoseResult Result, DoseDto? Item, string? Error)> TakeAsNeededAsync(
        Guid actorId, Guid courseId, PrnRequest request, CancellationToken ct = default)
    {
        var (course, access, _) = await courseAccess.LoadAsync(actorId, courseId, tracking: true, ct);
        if (course is null) return (DoseResult.NotFound, null, null);
        if (access != CourseAccess.Full) return (DoseResult.Forbidden, null, null);
        var gate = CheckActive(course);
        if (gate is not null) return (DoseResult.Conflict, null, gate);

        var schedule = MedicationCourseRules.ParseSchedule(course.ScheduleJson);
        if (schedule is not { Mode: DoseScheduleMode.AsNeeded }) return (DoseResult.Invalid, null, "Курс задан по расписанию.");

        var now = DateTime.UtcNow;
        var taken = request.TakenAt is { } t ? NormalizeUtc(t) : NormalizeUtc(now);
        var takenError = ValidateTakenAt(taken, now);
        if (takenError is not null) return (DoseResult.Invalid, null, takenError);

        var tz = TimeZones.Resolve(course.TimeZoneId);
        var day = DoseScheduleExpander.LocalDate(taken, tz);
        var dayStart = DoseScheduleExpander.ToUtc(day, TimeOnly.MinValue, tz);
        var dayEnd = DoseScheduleExpander.ToUtc(day.AddDays(1), TimeOnly.MinValue, tz);
        var takenToday = await db.MedicationDoses.CountAsync(d =>
            d.CourseId == courseId && d.ScheduledAt == null && d.Status == DoseStatus.Taken
            && d.TakenAt >= dayStart && d.TakenAt < dayEnd, ct);
        var limit = schedule.MaxPerDay ?? 1;
        if (takenToday >= limit && !request.Force)
            return (DoseResult.OverLimit, null, $"Сегодня уже принято {takenToday} из {limit}.");

        var dose = new MedicationDose
        {
            Id = Guid.NewGuid(),
            CourseId = courseId,
            ScheduledAt = null,
            Units = schedule.AsNeededUnits,
            Status = DoseStatus.Pending,
            CreatedAt = now,
        };
        db.MedicationDoses.Add(dose);
        return await ExecuteAsync(actorId, course, dose, DoseAction.Taken, taken, ct);
    }

    /// <summary>Отменить отметку «принял»/«пропустил»: вернуть таблетки в аптечку, убрать запись
    /// дневника, вернуть приёму состояние по часам.</summary>
    public async Task<DoseResult> UndoAsync(Guid actorId, Guid doseId, CancellationToken ct = default)
    {
        var dose = await db.MedicationDoses.FirstOrDefaultAsync(d => d.Id == doseId, ct);
        if (dose is null) return DoseResult.NotFound;

        var (course, access, _) = await courseAccess.LoadAsync(actorId, dose.CourseId, tracking: true, ct);
        if (course is null) return DoseResult.NotFound;
        if (access != CourseAccess.Full) return DoseResult.Forbidden;
        if (dose.Status is not (DoseStatus.Taken or DoseStatus.Skipped)) return DoseResult.Conflict;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (dose.WriteOffMedicationId is { } medicationId && dose.WriteOffUnits is > 0)
            await stock.TryAdjustAsync(medicationId, dose.WriteOffUnits.Value, ct);
        if (dose.HealthNoteId is { } noteId)
            await db.HealthNotes.Where(n => n.Id == noteId).ExecuteDeleteAsync(ct);

        if (dose.ScheduledAt is not { } scheduled)
        {
            db.MedicationDoses.Remove(dose); // «по необходимости»: без плана нечего возвращать в «ожидание»
        }
        else
        {
            var expired = DateTime.UtcNow >= DoseTiming.MissedDeadline(scheduled, course.MissedAfterMinutes);
            dose.Status = expired ? DoseStatus.Missed : DoseStatus.Pending;
            dose.TakenAt = null;
            dose.SnoozedUntil = null;
            dose.ActedByUserId = null;
            dose.HealthNoteId = null;
            dose.WriteOffMedicationId = null;
            dose.WriteOffUnits = null;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return DoseResult.Success;
    }

    private async Task<(DoseResult Result, DoseDto? Item, string? Error)> ExecuteAsync(
        Guid actorId, MedicationCourse course, MedicationDose dose, DoseAction action, DateTime? takenAt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        switch (action)
        {
            case DoseAction.Taken:
                if (dose.Status == DoseStatus.Taken) return (DoseResult.Success, ToDto(dose), null); // повтор — идемпотентно
                var taken = takenAt is { } t ? NormalizeUtc(t) : NormalizeUtc(now);
                var takenError = ValidateTakenAt(taken, now);
                if (takenError is not null) return (DoseResult.Invalid, null, takenError);

                await using (var tx = await db.Database.BeginTransactionAsync(ct))
                {
                    await MarkTakenAsync(actorId, course, dose, taken, ct);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }
                await MarkNotificationsReadAsync(dose.Id, ct);
                logger.LogInformation("Приём {DoseId} курса {CourseId} отмечен пользователем {UserId}", dose.Id, course.Id, actorId);
                return (DoseResult.Success, ToDto(dose), null);

            case DoseAction.Snooze10:
            case DoseAction.Snooze30:
                if (dose.Status is not (DoseStatus.Pending or DoseStatus.Snoozed))
                    return (DoseResult.Conflict, null, "Этот приём уже отмечен или пропущен.");
                dose.Status = DoseStatus.Snoozed;
                dose.SnoozedUntil = now.AddMinutes(action == DoseAction.Snooze10 ? 10 : 30);
                dose.SnoozeCount++;
                dose.ActedByUserId = actorId;
                break;

            case DoseAction.Skip:
                if (dose.Status == DoseStatus.Taken) return (DoseResult.Conflict, null, "Приём уже отмечен — сначала отмените отметку.");
                dose.Status = DoseStatus.Skipped;
                dose.SnoozedUntil = null;
                dose.ActedByUserId = actorId;
                break;

            default:
                return (DoseResult.Invalid, null, "Неизвестное действие.");
        }

        await db.SaveChangesAsync(ct);
        await MarkNotificationsReadAsync(dose.Id, ct);
        return (DoseResult.Success, ToDto(dose), null);
    }

    /// <summary>Мутации «принял»: статус, списание остатка, запись дневника. Без SaveChanges — общий
    /// коммит с вызывающим.</summary>
    private async Task MarkTakenAsync(
        Guid actorId, MedicationCourse course, MedicationDose dose, DateTime takenAt, CancellationToken ct)
    {
        dose.Status = DoseStatus.Taken;
        dose.TakenAt = takenAt;
        dose.SnoozedUntil = null;
        dose.ActedByUserId = actorId;

        if (course.WriteOffEnabled && course.MedicationId is { } medicationId)
        {
            var info = await stock.GetAsync(medicationId, ct);
            // Списывать можно только из аптечки своей семьи: курс мог быть привязан раньше, а человек — выйти из семьи.
            if (info is not null && await stock.CanUseAsync(actorId, info.FamilyId, ct))
            {
                var applied = await stock.TryAdjustAsync(medicationId, -dose.Units, ct);
                if (applied is > 0)
                {
                    dose.WriteOffMedicationId = medicationId;
                    dose.WriteOffUnits = applied;
                }
            }
        }

        // Дневник строго личный: пишем только собственные курсы, за подопечного отметку делает другой человек.
        if (course.SubjectUserId == actorId)
        {
            var note = notes.StageIntake(actorId, course.DrugName, DoseFormat.Units(dose.Units, course.DoseUnit), takenAt);
            dose.HealthNoteId = note.Id;
        }
    }

    private Task<int> MarkNotificationsReadAsync(Guid doseId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Иначе каждый повтор напоминания копился бы в счётчике непрочитанного.
        return db.Notifications
            .Where(n => n.RelatedEntityId == doseId && n.Type == NotificationType.MedicationDoseDue && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now), ct);
    }

    /// <summary>Проверяет, что <paramref name="scheduled"/> — реальный плановый приём курса, и строит строку.</summary>
    private static (MedicationDose? Dose, string? Error) TryCreateDose(MedicationCourse course, DateTime scheduled)
    {
        var schedule = MedicationCourseRules.ParseSchedule(course.ScheduleJson);
        if (schedule is null) return (null, "Расписание курса повреждено.");

        var now = DateTime.UtcNow;
        if (scheduled < course.EffectiveFromUtc) return (null, "Этот приём был до изменения расписания.");
        if (scheduled > now + EarlyTolerance) return (null, "Этот приём ещё не скоро.");

        var tz = TimeZones.Resolve(course.TimeZoneId);
        var occurrence = DoseScheduleExpander
            .Expand(schedule, course.StartDate, course.EndDate, tz, scheduled, scheduled.AddSeconds(1))
            .FirstOrDefault(o => o.ScheduledAtUtc == scheduled);
        if (occurrence is null) return (null, "В это время приёма по расписанию нет.");

        return (new MedicationDose
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            ScheduledAt = scheduled,
            Units = occurrence.Units,
            Status = DoseStatus.Pending,
            CreatedAt = now,
        }, null);
    }

    private static string? CheckActive(MedicationCourse course) => course.Status switch
    {
        MedicationCourseStatus.Active => null,
        MedicationCourseStatus.Paused => "Курс приостановлен.",
        _ => "Курс завершён.",
    };

    private static string? ValidateTakenAt(DateTime takenAt, DateTime now) =>
        takenAt < EarliestAllowed || takenAt > now + FutureTakenTolerance ? "Время приёма указано неверно." : null;

    private static DoseDto ToDto(MedicationDose d) =>
        new(d.Id, d.Status, d.ScheduledAt, d.TakenAt, d.SnoozedUntil, d.Units, d.WriteOffUnits is > 0);

    /// <summary>Npgsql timestamptz принимает только Kind=Utc; Unspecified с клиента трактуем как UTC.
    /// Секунды отбрасываем: плановое время — целая минута, а сравнивать надо точно.</summary>
    internal static DateTime NormalizeUtc(DateTime d)
    {
        var utc = d.Kind switch
        {
            DateTimeKind.Utc => d,
            DateTimeKind.Local => d.ToUniversalTime(),
            _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        };
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}
