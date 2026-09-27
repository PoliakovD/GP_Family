using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.HealthNotes;
using FamilyHub.Domain.Vaccinations;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>
/// Прививки: график вычисляется на лету (<see cref="VaccinationScheduleCalculator"/>) по каталогу,
/// дате рождения человека и его фактам — сам сервис хранит и правит только факты. Доступ — тот же
/// уровень, что у курсов приёма лекарств (ADR-0015/ADR-0016): свой/подопечный семьи (полностью),
/// наблюдаемый взрослый (только чтение).
/// </summary>
public class VaccinationService(
    AppDbContext db, VaccinationAccess access, VaccinationSubjects subjects, IFileStorage storage,
    ILogger<VaccinationService> logger)
{
    public async Task<OverviewDto> GetOverviewAsync(Guid userId, CancellationToken ct = default)
    {
        var scope = await access.GetScopeAsync(userId, ct);
        var people = await subjects.ListVisibleAsync(userId, scope, ct);
        var today = Today();

        var attention = new List<AttentionCardDto>();
        var summaries = new List<SubjectSummaryDto>();

        foreach (var person in people)
        {
            var subjectDto = ToSubjectDto(person);
            if (person.BirthDate is null)
            {
                summaries.Add(new SubjectSummaryDto(subjectDto, 0, 0, 0, 0, 0, "Не указана дата рождения — уточните в профиле"));
                continue;
            }

            var facts = await LoadFactsAsync(person, ct);
            var items = VaccinationScheduleCalculator.Calculate(person.BirthDate.Value, facts, today);

            foreach (var item in items.Where(i => i.Status is VaccinationStatus.DueSoon or VaccinationStatus.CanDo))
                attention.Add(new AttentionCardDto(subjectDto, ToDto(item)));

            var relevant = items.Where(i => i.Status != VaccinationStatus.Upcoming).ToList();
            var done = relevant.Count(i => i.Status is VaccinationStatus.Done or VaccinationStatus.HadDisease);
            var dueSoon = relevant.Count(i => i.Status == VaccinationStatus.DueSoon);
            var canDo = relevant.Count(i => i.Status == VaccinationStatus.CanDo);
            var noData = relevant.Count(i => i.Status == VaccinationStatus.NoData);

            var hint = noData switch
            {
                > 0 when !person.IsSelf => "детские прививки не внесены — загрузить сертификат",
                _ => null,
            };
            summaries.Add(new SubjectSummaryDto(subjectDto, done, dueSoon, canDo, noData, relevant.Count, hint));
        }

        return new OverviewDto(
            attention.OrderBy(a => a.Item.WindowTo ?? a.Item.WindowFrom ?? DateOnly.MaxValue).ToList(),
            summaries);
    }

    public async Task<int> AttentionCountAsync(Guid userId, CancellationToken ct = default) =>
        (await GetOverviewAsync(userId, ct)).Attention.Count;

    public async Task<(VaccinationResult Result, PersonScheduleDto? Item)> GetPersonScheduleAsync(
        Guid userId, string kind, Guid id, CancellationToken ct = default)
    {
        var scope = await access.GetScopeAsync(userId, ct);
        var subject = await subjects.FindAsync(userId, kind, id, scope, ct);
        if (subject is null) return (VaccinationResult.NotFound, null);

        var subjectDto = ToSubjectDto(subject);
        var custom = (await LoadCustomAsync(subject, ct)).Select(ToCustomScheduleItemDto).ToList();

        if (subject.BirthDate is null)
            return (VaccinationResult.Success, new PersonScheduleDto(subjectDto, null, null, false, [], [], custom));

        var facts = await LoadFactsAsync(subject, ct);
        var today = Today();
        var (years, months) = AgeOf(subject.BirthDate.Value, today);
        var items = VaccinationScheduleCalculator.Calculate(subject.BirthDate.Value, facts, today).Select(ToDto).ToList();

        var byDisease = items
            .SelectMany(i => (VaccineCatalog.Find(i.SeriesCode)?.Diseases ?? [i.SeriesName]).Select(d => (Disease: d, Item: i)))
            .GroupBy(x => x.Disease)
            .Select(g => new DiseaseGroupDto(g.Key, g.Select(x => x.Item).ToList()))
            .OrderBy(g => g.Disease, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return (VaccinationResult.Success, new PersonScheduleDto(subjectDto, years, months, true, items, byDisease, custom));
    }

    public async Task<(VaccinationResult Result, SeriesDetailDto? Item)> GetSeriesDetailAsync(
        Guid userId, string kind, Guid id, string seriesCode, CancellationToken ct = default)
    {
        var series = VaccineCatalog.Find(seriesCode);
        if (series is null) return (VaccinationResult.NotFound, null);

        var scope = await access.GetScopeAsync(userId, ct);
        var subject = await subjects.FindAsync(userId, kind, id, scope, ct);
        if (subject is null || subject.BirthDate is null) return (VaccinationResult.NotFound, null);

        var facts = await LoadFactsAsync(subject, ct);
        var items = VaccinationScheduleCalculator.Calculate(subject.BirthDate.Value, facts, Today())
            .Where(i => i.SeriesCode == seriesCode)
            .ToList();
        if (items.Count == 0) return (VaccinationResult.NotFound, null);

        var recordIds = items.Where(i => i.RecordId is not null).Select(i => i.RecordId!.Value).ToList();
        var vaccineNames = recordIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await db.Vaccinations.AsNoTracking().Where(v => recordIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.VaccineName, ct);
        var filesByRecord = recordIds.Count == 0
            ? new Dictionary<Guid, List<AttachmentRefDto>>()
            : (await db.FileAttachments.AsNoTracking()
                    .Where(a => a.OwnerType == FileOwnerType.Vaccination && recordIds.Contains(a.OwnerId))
                    .ToListAsync(ct))
                .GroupBy(a => a.OwnerId)
                .ToDictionary(g => g.Key, g => g.Select(a => new AttachmentRefDto(a.Id, a.FileName, a.SizeBytes, a.UploadedAt)).ToList());

        var doses = new List<DoseDetailDto>();
        foreach (var item in items)
        {
            var diary = subject.IsSelf && item.RecordId is not null && item.Date is { } date
                ? await LoadDiaryForDateAsync(userId, date, series.ReactionWindowDays, ct)
                : [];
            doses.Add(new DoseDetailDto(
                item.DoseIndex, item.Label, item.Status, item.WindowFrom, item.WindowTo, item.RecordId, item.Date,
                item.RecordId is { } rid ? vaccineNames.GetValueOrDefault(rid) : null,
                item.RecordId is { } rid2 ? filesByRecord.GetValueOrDefault(rid2, []) : [],
                diary));
        }

        return (VaccinationResult.Success, new SeriesDetailDto(
            series.Code, series.Name, series.Diseases, series.Group, series.About, series.Contraindications,
            series.TradeNames, series.ReactionHint, doses));
    }

    public async Task<(VaccinationResult Result, CustomVaccinationDetailDto? Item)> GetCustomDetailAsync(
        Guid userId, Guid id, CancellationToken ct = default)
    {
        var (item, level, _) = await access.LoadAsync(userId, id, tracking: false, ct);
        if (item is null) return (VaccinationResult.NotFound, null);

        var files = await db.FileAttachments.AsNoTracking()
            .Where(a => a.OwnerType == FileOwnerType.Vaccination && a.OwnerId == id)
            .Select(a => new AttachmentRefDto(a.Id, a.FileName, a.SizeBytes, a.UploadedAt))
            .ToListAsync(ct);

        List<DiaryLinkDto> diary = [];
        if (item.SubjectUserId == userId && item.Date is { } date)
        {
            var window = VaccineCatalog.Find(item.SeriesCode)?.ReactionWindowDays ?? VaccinationRules.WellbeingCheckDays * 2;
            diary = await LoadDiaryForDateAsync(userId, date, window, ct);
        }

        var name = item.SeriesCode is not null ? VaccineCatalog.Find(item.SeriesCode)?.Name ?? item.SeriesCode : item.CustomName ?? "Прививка";
        return (VaccinationResult.Success, new CustomVaccinationDetailDto(
            item.Id, name, item.VaccineName, item.Kind, item.Date, item.DatePrecision, files, diary,
            level == VaccinationAccessLevel.Full));
    }

    public async Task<(VaccinationResult Result, ScheduleItemDto? Item, string? Error)> CreateAsync(
        Guid userId, CreateVaccinationRequest request, CancellationToken ct = default)
    {
        var scope = await access.GetScopeAsync(userId, ct);
        var subject = await subjects.FindAsync(userId, request.SubjectKind, request.SubjectId, scope, ct);
        if (subject is null) return (VaccinationResult.NotFound, null, null);
        if (!subject.CanEdit) return (VaccinationResult.Forbidden, null, null);

        var content = new VaccinationContent(
            request.SeriesCode, request.DoseIndex, request.CustomName, request.VaccineName, request.Kind,
            request.Date, request.DatePrecision, subject.BirthDate, Today());
        var error = VaccinationRules.Validate(content);
        if (error is not null) return (VaccinationResult.Invalid, null, error);

        var now = DateTime.UtcNow;
        var existing = await FindExistingAsync(subject, request.SeriesCode, request.DoseIndex, ct);

        var wellbeingAt = request.RequestWellbeingCheck && subject.IsSelf ? now.AddDays(VaccinationRules.WellbeingCheckDays) : (DateTime?)null;
        var vaccineName = string.IsNullOrWhiteSpace(request.VaccineName) ? null : request.VaccineName.Trim();
        var date = request.Kind == VaccinationKind.Unknown ? null : request.Date;
        var precision = request.Kind == VaccinationKind.Unknown ? null : request.DatePrecision;

        Vaccination saved;
        if (existing is not null)
        {
            existing.VaccineName = vaccineName;
            existing.Kind = request.Kind;
            existing.Date = date;
            existing.DatePrecision = precision;
            existing.CertificateId = request.CertificateId ?? existing.CertificateId;
            if (wellbeingAt is not null) existing.WellbeingCheckAt = wellbeingAt;
            existing.UpdatedAt = now;
            saved = existing;
        }
        else
        {
            saved = new Vaccination
            {
                Id = Guid.NewGuid(),
                SubjectUserId = subject.Kind == VaccinationSubjects.UserKind ? subject.Id : null,
                FamilyDependentId = subject.Kind == VaccinationSubjects.DependentKind ? subject.Id : null,
                FamilyId = subject.FamilyId,
                CreatedByUserId = userId,
                SeriesCode = request.SeriesCode,
                DoseIndex = request.DoseIndex,
                CustomName = request.SeriesCode is null ? request.CustomName?.Trim() : null,
                VaccineName = vaccineName,
                Kind = request.Kind,
                Date = date,
                DatePrecision = precision,
                CertificateId = request.CertificateId,
                WellbeingCheckAt = wellbeingAt,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Vaccinations.Add(saved);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Прививка {Id} сохранена для {Kind}:{SubjectId} пользователем {UserId}", saved.Id, subject.Kind, subject.Id, userId);

        ScheduleItemDto? dto = null;
        if (subject.BirthDate is { } birth && request.SeriesCode is not null)
        {
            var facts = await LoadFactsAsync(subject, ct);
            var item = VaccinationScheduleCalculator.Calculate(birth, facts, Today())
                .FirstOrDefault(i => i.SeriesCode == request.SeriesCode && i.DoseIndex == request.DoseIndex);
            if (item is not null) dto = ToDto(item);
        }
        else if (request.SeriesCode is null)
        {
            dto = ToCustomScheduleItemDto(saved);
        }

        return (VaccinationResult.Success, dto, null);
    }

    public async Task<(VaccinationResult Result, BulkMarkResultDto? Item, string? Error)> BulkMarkAsync(
        Guid userId, BulkMarkRequest request, CancellationToken ct = default)
    {
        var scope = await access.GetScopeAsync(userId, ct);
        var subject = await subjects.FindAsync(userId, request.SubjectKind, request.SubjectId, scope, ct);
        if (subject is null) return (VaccinationResult.NotFound, null, null);
        if (!subject.CanEdit) return (VaccinationResult.Forbidden, null, null);
        if (request.Items.Count == 0) return (VaccinationResult.Invalid, null, "Отметьте хотя бы одну прививку.");

        var now = DateTime.UtcNow;
        var saved = 0;
        foreach (var entry in request.Items)
        {
            if (VaccineCatalog.Find(entry.SeriesCode) is null || entry.DoseIndex < 0) continue;

            var content = new VaccinationContent(
                entry.SeriesCode, entry.DoseIndex, null, null, entry.Kind, entry.Date, entry.DatePrecision, subject.BirthDate, Today());
            if (VaccinationRules.Validate(content) is not null) continue;

            var date = entry.Kind == VaccinationKind.Unknown ? null : entry.Date;
            var precision = entry.Kind == VaccinationKind.Unknown ? null : entry.DatePrecision;
            var existing = await FindExistingAsync(subject, entry.SeriesCode, entry.DoseIndex, ct);

            if (existing is not null)
            {
                existing.Kind = entry.Kind;
                existing.Date = date;
                existing.DatePrecision = precision;
                existing.CertificateId = request.CertificateId ?? existing.CertificateId;
                existing.UpdatedAt = now;
            }
            else
            {
                db.Vaccinations.Add(new Vaccination
                {
                    Id = Guid.NewGuid(),
                    SubjectUserId = subject.Kind == VaccinationSubjects.UserKind ? subject.Id : null,
                    FamilyDependentId = subject.Kind == VaccinationSubjects.DependentKind ? subject.Id : null,
                    FamilyId = subject.FamilyId,
                    CreatedByUserId = userId,
                    SeriesCode = entry.SeriesCode,
                    DoseIndex = entry.DoseIndex,
                    Kind = entry.Kind,
                    Date = date,
                    DatePrecision = precision,
                    CertificateId = request.CertificateId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            saved++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Массовая отметка прививок: {Saved} записей для {Kind}:{SubjectId} пользователем {UserId}",
            saved, subject.Kind, subject.Id, userId);
        return (VaccinationResult.Success, new BulkMarkResultDto(saved), null);
    }

    public async Task<VaccinationResult> UpdateAsync(Guid userId, Guid id, UpdateVaccinationRequest request, CancellationToken ct = default)
    {
        var (item, level, _) = await access.LoadAsync(userId, id, tracking: true, ct);
        if (item is null) return VaccinationResult.NotFound;
        if (level != VaccinationAccessLevel.Full) return VaccinationResult.Forbidden;

        var birth = await BirthDateOfAsync(item, ct);
        var content = new VaccinationContent(
            item.SeriesCode, item.DoseIndex, item.CustomName, request.VaccineName, request.Kind,
            request.Date, request.DatePrecision, birth, Today());
        if (VaccinationRules.Validate(content) is not null) return VaccinationResult.Invalid;

        item.VaccineName = string.IsNullOrWhiteSpace(request.VaccineName) ? null : request.VaccineName.Trim();
        item.Kind = request.Kind;
        item.Date = request.Kind == VaccinationKind.Unknown ? null : request.Date;
        item.DatePrecision = request.Kind == VaccinationKind.Unknown ? null : request.DatePrecision;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return VaccinationResult.Success;
    }

    /// <summary>Переключатель «напомнить о самочувствии» в шторке после сохранения — узкое
    /// обновление одного поля, не общий Update (тот пришлось бы кормить полным Kind/Date/
    /// DatePrecision, которых нет на руках у фронта в этот момент — только что вернувшийся
    /// ScheduleItemDto их не несёт). Только для своих прививок (подопечным дневник недоступен,
    /// см. ADR-0016 §5).</summary>
    public async Task<VaccinationResult> SetWellbeingCheckAsync(Guid userId, Guid id, bool requested, CancellationToken ct = default)
    {
        var (item, level, _) = await access.LoadAsync(userId, id, tracking: true, ct);
        if (item is null) return VaccinationResult.NotFound;
        if (level != VaccinationAccessLevel.Full || item.SubjectUserId != userId) return VaccinationResult.Forbidden;

        item.WellbeingCheckAt = requested ? DateTime.UtcNow.AddDays(VaccinationRules.WellbeingCheckDays) : null;
        if (!requested) item.WellbeingCheckSent = false;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return VaccinationResult.Success;
    }

    public async Task<VaccinationResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var (item, level, _) = await access.LoadAsync(userId, id, tracking: true, ct);
        if (item is null) return VaccinationResult.NotFound;
        if (level != VaccinationAccessLevel.Full) return VaccinationResult.Forbidden;

        var attachments = await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.Vaccination && a.OwnerId == id).ToListAsync(ct);
        var keys = attachments.Select(a => a.StorageKey).ToList();

        db.FileAttachments.RemoveRange(attachments);
        db.Vaccinations.Remove(item);
        await db.SaveChangesAsync(ct);

        // Файлы — после коммита БД: сбой удаления объекта не откатывает удаление записи.
        foreach (var key in keys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Удаление прививки {Id}: не удалось удалить блоб {StorageKey}", id, key);
            }
        }

        logger.LogInformation("Прививка {Id} удалена пользователем {UserId}", id, userId);
        return VaccinationResult.Success;
    }

    public static List<CatalogSeriesDto> GetCatalog() =>
        VaccineCatalog.All.Select(s => new CatalogSeriesDto(
                s.Code, s.Name, s.ShortName, s.Group, s.Diseases,
                s.Doses.Select(d => new CatalogDoseDto(d.Label, d.IsAgeBased)).ToList(),
                s.TradeNames, s.About, s.Contraindications, s.ReactionHint, s.RepeatEveryYears, s.SeasonalWindow is not null,
                s.ClosedByDisease))
            .ToList();

    // ---- Внутреннее ----

    private async Task<Vaccination?> FindExistingAsync(VaccinationSubjectInfo subject, string? seriesCode, int? doseIndex, CancellationToken ct)
    {
        if (seriesCode is null) return null;
        return subject.Kind == VaccinationSubjects.UserKind
            ? await db.Vaccinations.FirstOrDefaultAsync(v => v.SubjectUserId == subject.Id && v.SeriesCode == seriesCode && v.DoseIndex == doseIndex, ct)
            : await db.Vaccinations.FirstOrDefaultAsync(v => v.FamilyDependentId == subject.Id && v.SeriesCode == seriesCode && v.DoseIndex == doseIndex, ct);
    }

    private async Task<DateOnly?> BirthDateOfAsync(Vaccination v, CancellationToken ct)
    {
        if (v.SubjectUserId is { } uid) return await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.BirthDate).FirstOrDefaultAsync(ct);
        if (v.FamilyDependentId is { } did) return await db.FamilyDependents.AsNoTracking().Where(d => d.Id == did).Select(d => d.BirthDate).FirstOrDefaultAsync(ct);
        return null;
    }

    private async Task<List<VaccinationFact>> LoadFactsAsync(VaccinationSubjectInfo person, CancellationToken ct)
    {
        var query = person.Kind == VaccinationSubjects.UserKind
            ? db.Vaccinations.AsNoTracking().Where(v => v.SubjectUserId == person.Id)
            : db.Vaccinations.AsNoTracking().Where(v => v.FamilyDependentId == person.Id);
        var rows = await query.Where(v => v.SeriesCode != null).ToListAsync(ct);
        return rows.Select(v => new VaccinationFact(v.Id, v.SeriesCode!, v.DoseIndex, v.Kind, v.Date, v.DatePrecision)).ToList();
    }

    private async Task<List<Vaccination>> LoadCustomAsync(VaccinationSubjectInfo person, CancellationToken ct)
    {
        var query = person.Kind == VaccinationSubjects.UserKind
            ? db.Vaccinations.AsNoTracking().Where(v => v.SubjectUserId == person.Id)
            : db.Vaccinations.AsNoTracking().Where(v => v.FamilyDependentId == person.Id);
        return await query.Where(v => v.SeriesCode == null).OrderByDescending(v => v.CreatedAt).ToListAsync(ct);
    }

    private async Task<List<DiaryLinkDto>> LoadDiaryForDateAsync(Guid userId, DateOnly date, int windowDays, CancellationToken ct)
    {
        var from = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = from.AddDays(windowDays + 1);
        var notes = await db.HealthNotes.AsNoTracking()
            .Where(n => n.OwnerUserId == userId && n.OccurredAt >= from && n.OccurredAt < to
                        && (n.Kind == HealthNoteKind.Symptom || n.Kind == HealthNoteKind.Metric))
            .OrderBy(n => n.OccurredAt)
            .ToListAsync(ct);

        return notes.Select(n =>
        {
            var content = HealthNoteRules.ParseContent(n.Kind, n.Title, n.Text, n.DataJson);
            var text = n.Kind == HealthNoteKind.Metric && content.Metric is { } m
                ? $"{HealthMetricCatalog.Find(m.Code)?.Name ?? m.Code}: {m.Value}"
                : content.Title ?? content.Text ?? "Запись дневника";
            return new DiaryLinkDto(n.OccurredAt, n.Kind.ToString(), text);
        }).ToList();
    }

    private static SubjectDto ToSubjectDto(VaccinationSubjectInfo p) => new(p.Kind, p.Id, p.Name, p.IsSelf, p.CanEdit);

    private static ScheduleItemDto ToDto(ScheduleItem i) => new(
        i.SeriesCode, i.DoseIndex, i.Label, i.SeriesName, i.SeriesShortName, i.Group, i.Stage, i.Status,
        i.WindowFrom, i.WindowTo, i.RecordId, i.Date, i.IsRepeat);

    private static ScheduleItemDto ToCustomScheduleItemDto(Vaccination v) => new(
        "", -2, v.CustomName ?? "Прививка", v.CustomName ?? "Прививка", v.CustomName ?? "Прививка",
        VaccineGroup.Epidemic, "custom",
        v.Kind switch
        {
            VaccinationKind.Done => VaccinationStatus.Done,
            VaccinationKind.HadDisease => VaccinationStatus.HadDisease,
            _ => VaccinationStatus.NoData,
        },
        null, null, v.Id, v.Date, false);

    private static (int Years, int Months) AgeOf(DateOnly birth, DateOnly today)
    {
        var months = (today.Year - birth.Year) * 12 + (today.Month - birth.Month);
        if (today.Day < birth.Day) months--;
        if (months < 0) months = 0;
        return (months / 12, months % 12);
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);
}
