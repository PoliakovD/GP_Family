using System.Globalization;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;
using DomainLabIndicator = FamilyHub.Domain.Entities.LabIndicator;

namespace FamilyHub.Modules.Medical.Extraction;

public enum ExtractionQueryResult { Success, NotFound, Forbidden }

/// <summary>
/// Чтение результатов конвейера извлечения (ветка medicalrecords). Показатели/статус/summary
/// наследуют видимость родительской мед-записи — своей у них нет, тот же принцип, что у вложений
/// (см. AttachmentService.GetForMedicalRecordAsync): просмотр чужой расшаренной записи пишет аудит.
/// </summary>
public class ExtractionQueryService(
    AppDbContext db, MedicalRecordService medicalRecords, Kb.KbLookupService medicationKbLookup, IMedicalAuditWriter audit)
{
    public async Task<(ExtractionQueryResult Result, ExtractionStatusResponse? Item)> GetStatusAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var job = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .Where(j => j.MedicalRecordId == recordId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (job is null) return (ExtractionQueryResult.NotFound, null);

        return (ExtractionQueryResult.Success, new ExtractionStatusResponse(
            job.Status, job.Stage, job.IndicatorCount, job.Error, job.TotalFiles, job.ProcessedFiles, job.CreatedAt, job.CompletedAt));
    }

    public async Task<(ExtractionQueryResult Result, List<IndicatorDto> Items)> GetIndicatorsAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, []);

        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.MedicalRecordId == recordId)
            .OrderBy(i => i.Position)
            .ToListAsync(ct);

        return (ExtractionQueryResult.Success, items.Select(ToDto).ToList());
    }

    /// <summary>Заключение врача (Kind=DoctorVisit) — MedicalRecord.ExtractedDataJson, зеркало
    /// GetSummaryAsync для показателей анализа (Kind=Analysis использует SummaryJson, не это поле).
    /// Ссылки на справочник медикаментов (KbMedicationId) резолвятся ЖИВЫМ поиском на каждое
    /// чтение (см. PrescribedMedicationDto) — так подхватывается результат обогащения, даже если
    /// оно завершилось уже после первого просмотра заключения, без отдельного бэкофилла.</summary>
    public async Task<(ExtractionQueryResult Result, VisitConclusionResponse? Item)> GetConclusionAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var extractedDataJson = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => r.ExtractedDataJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(extractedDataJson)) return (ExtractionQueryResult.NotFound, null);

        var conclusion = JsonSerializer.Deserialize<VisitConclusion>(extractedDataJson);
        if (conclusion is null) return (ExtractionQueryResult.NotFound, null);

        var medications = new List<PrescribedMedicationDto>();
        foreach (var med in conclusion.PrescribedMedications ?? [])
        {
            var normalizedName = MedicationNameNormalizer.Normalize(med.Name);
            Guid? kbMedicationId = null;
            if (normalizedName.Length > 0)
            {
                var lookup = await medicationKbLookup.LookupAsync(normalizedName, ct);
                if (lookup.Kind == Kb.KbLookupKind.Hit) kbMedicationId = lookup.KbId;
            }
            medications.Add(new PrescribedMedicationDto(med.Name, med.DosageInstructions, kbMedicationId));
        }

        return (ExtractionQueryResult.Success, new VisitConclusionResponse(
            conclusion.Diagnosis, conclusion.Recommendations, conclusion.Anamnesis, conclusion.ProceduresPerformed, medications));
    }

    public async Task<(ExtractionQueryResult Result, RecordSummaryResponse? Item)> GetSummaryAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var summaryJson = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => r.SummaryJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(summaryJson)) return (ExtractionQueryResult.NotFound, null);

        var summary = JsonSerializer.Deserialize<LabSummary>(summaryJson);
        if (summary is null) return (ExtractionQueryResult.NotFound, null);

        return (ExtractionQueryResult.Success, new RecordSummaryResponse(
            summary.PlainSummary, summary.Deviations, summary.QuestionsForDoctor, summary.Disclaimer));
    }

    /// <summary>Последнее значение по каждому (показатель, биоматериал) среди СВОИХ записей
    /// пользователя (владелец) — расшаренные чужие записи сюда не входят, "мои показатели" в
    /// буквальном смысле. (Specimen, SpecimenCustomId) — часть ключа группировки (v2 + UX-
    /// редизайн): лейкоциты крови и мочи, а также два разных кастомных биоматериала (оба
    /// Specimen=Other) не должны схлопнуться в одну строку.</summary>
    public async Task<List<MyIndicatorSummary>> GetMyIndicatorsAsync(Guid userId, CancellationToken ct = default)
    {
        var all = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId)
            .ToListAsync(ct);

        return all
            .GroupBy(i => (i.AnalyteKey, i.Specimen, i.SpecimenCustomId))
            .Select(g => g.OrderByDescending(i => i.RecordDate).First())
            .Select(i => new MyIndicatorSummary(
                i.AnalyteKey, i.DisplayName, i.Specimen, i.ValueRaw, i.Unit, i.Flag, i.RecordDate, i.SpecimenCustomId))
            .OrderBy(s => s.DisplayName)
            .ToList();
    }

    public async Task<List<IndicatorHistoryPoint>> GetHistoryAsync(
        Guid userId, string analyteKey, SpecimenType specimen, Guid? specimenCustomId, CancellationToken ct = default)
    {
        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId && i.AnalyteKey == analyteKey && i.Specimen == specimen
                && i.SpecimenCustomId == specimenCustomId)
            .OrderBy(i => i.RecordDate)
            .ToListAsync(ct);

        return items.Select(i => new IndicatorHistoryPoint(i.RecordDate, i.ValueRaw, i.ValueNumericText, i.Flag, i.MedicalRecordId)).ToList();
    }

    /// <summary>Правка показателя вручную (ошибка OCR) — только владелец мед-записи. Ref-поля,
    /// присланные в запросе, становятся новым "референсом с бланка" (RefSource.Blank) — ручная
    /// правка семантически заменяет то, что распознала модель, тем же приоритетом, что и печатный
    /// бланк; KB/расчётный каскад заново не гоняется (пользователь правит конкретные цифры, а не
    /// просит переопределить справочником). Flag пересчитывается тем же компаратором, что и при
    /// автораспознавании — не дублируем пороговую логику.</summary>
    public async Task<UpdateIndicatorResult> UpdateIndicatorAsync(
        Guid indicatorId, Guid userId, UpdateIndicatorRequest request, CancellationToken ct = default)
    {
        var indicator = await db.LabIndicators.FirstOrDefaultAsync(i => i.Id == indicatorId, ct);
        if (indicator is null) return UpdateIndicatorResult.NotFound;
        if (indicator.OwnerUserId != userId) return UpdateIndicatorResult.Forbidden;

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0) return UpdateIndicatorResult.NotFound;

        var analyteKey = LabAnalyteNormalizer.Normalize(displayName);
        if (analyteKey.Length == 0) analyteKey = indicator.AnalyteKey;

        // Кастомный биоматериал (UX-редизайн) — только при Specimen=Other, только свой (нельзя
        // сослаться на чужую запись справочника, зная только id); иначе принудительно обнуляем,
        // как FamilyDependent.PetSpecies при IsPet=false.
        var specimenCustomId = request.Specimen == SpecimenType.Other ? request.SpecimenCustomId : null;
        if (specimenCustomId is { } customId &&
            !await db.UserSpecimens.AnyAsync(s => s.Id == customId && s.OwnerUserId == userId, ct))
            return UpdateIndicatorResult.NotFound;

        // Уникальный индекс (MedicalRecordId, AnalyteKey, Specimen, SpecimenCustomId) — правка
        // могла увести показатель на пару, уже занятую другой строкой этой же записи.
        var conflict = await db.LabIndicators.AnyAsync(i =>
            i.Id != indicatorId && i.MedicalRecordId == indicator.MedicalRecordId &&
            i.AnalyteKey == analyteKey && i.Specimen == request.Specimen && i.SpecimenCustomId == specimenCustomId, ct);
        if (conflict) return UpdateIndicatorResult.Conflict;

        var refLow = ParseNumeric(request.RefLowText);
        var refHigh = ParseNumeric(request.RefHighText);
        var refText = string.IsNullOrWhiteSpace(request.RefText) ? null : request.RefText.Trim();

        var dto = new ExtractedLabIndicator(displayName, request.ValueRaw, request.Unit, refLow, refHigh, refText);
        var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback: null, ageYears: null, sex: null);

        indicator.DisplayName = displayName;
        indicator.AnalyteKey = analyteKey;
        indicator.Specimen = request.Specimen;
        indicator.SpecimenCustomId = specimenCustomId;
        indicator.ValueRaw = request.ValueRaw;
        indicator.ValueNumericText = ParseNumeric(request.ValueRaw)?.ToString(CultureInfo.InvariantCulture);
        indicator.Unit = request.Unit;
        indicator.RefLowText = effLow?.ToString(CultureInfo.InvariantCulture);
        indicator.RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture);
        indicator.RefText = refText;
        indicator.Flag = flag;
        indicator.RefSource = refSource;

        await db.SaveChangesAsync(ct);
        return UpdateIndicatorResult.Success;
    }

    /// <summary>Ручное добавление показателя (UX-редизайн) — тот же путь расчёта флага, что и
    /// правка: RefSource.Blank, KB-каскад заново не гоняется (пользователь вводит конкретные
    /// цифры руками, не просит распознать заново). Position — в конец текущего списка записи.</summary>
    public async Task<(CreateIndicatorResult Result, IndicatorDto? Item)> CreateIndicatorAsync(
        Guid recordId, Guid userId, CreateIndicatorRequest request, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => new { r.Id, r.OwnerUserId }).FirstOrDefaultAsync(ct);
        if (record is null) return (CreateIndicatorResult.NotFound, null);
        if (record.OwnerUserId != userId) return (CreateIndicatorResult.Forbidden, null);

        var displayName = request.DisplayName.Trim();
        if (displayName.Length == 0) return (CreateIndicatorResult.NotFound, null);

        var analyteKey = LabAnalyteNormalizer.Normalize(displayName);
        if (analyteKey.Length == 0) return (CreateIndicatorResult.NotFound, null);

        var specimenCustomId = request.Specimen == SpecimenType.Other ? request.SpecimenCustomId : null;
        if (specimenCustomId is { } customId &&
            !await db.UserSpecimens.AnyAsync(s => s.Id == customId && s.OwnerUserId == userId, ct))
            return (CreateIndicatorResult.NotFound, null);

        var conflict = await db.LabIndicators.AnyAsync(i =>
            i.MedicalRecordId == recordId && i.AnalyteKey == analyteKey &&
            i.Specimen == request.Specimen && i.SpecimenCustomId == specimenCustomId, ct);
        if (conflict) return (CreateIndicatorResult.Conflict, null);

        var refLow = ParseNumeric(request.RefLowText);
        var refHigh = ParseNumeric(request.RefHighText);
        var refText = string.IsNullOrWhiteSpace(request.RefText) ? null : request.RefText.Trim();
        var dto = new ExtractedLabIndicator(displayName, request.ValueRaw, request.Unit, refLow, refHigh, refText);
        var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback: null, ageYears: null, sex: null);

        var maxPosition = await db.LabIndicators
            .Where(i => i.MedicalRecordId == recordId)
            .Select(i => (int?)i.Position)
            .MaxAsync(ct) ?? -1;

        var recordDate = await db.MedicalRecords.Where(r => r.Id == recordId).Select(r => r.RecordDate).FirstAsync(ct);

        var indicator = new DomainLabIndicator
        {
            Id = Guid.NewGuid(),
            MedicalRecordId = recordId,
            RecordDate = recordDate,
            OwnerUserId = userId,
            AnalyteKey = analyteKey,
            DisplayName = displayName,
            Flag = flag,
            RefSource = refSource,
            Specimen = request.Specimen,
            SpecimenCustomId = specimenCustomId,
            Position = maxPosition + 1,
            ValueRaw = request.ValueRaw,
            ValueNumericText = ParseNumeric(request.ValueRaw)?.ToString(CultureInfo.InvariantCulture),
            Unit = request.Unit,
            RefLowText = effLow?.ToString(CultureInfo.InvariantCulture),
            RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture),
            RefText = refText,
            CreatedAt = DateTime.UtcNow,
        };
        db.LabIndicators.Add(indicator);
        await db.SaveChangesAsync(ct);

        return (CreateIndicatorResult.Success, ToDto(indicator));
    }

    /// <summary>Удаление ошибочно распознанного/добавленного показателя — только владелец
    /// записи. Без него редактируемая таблица не покрывает основной сценарий правки: OCR иногда
    /// придумывает строку целиком, не только искажает значение в существующей.</summary>
    public async Task<DeleteIndicatorResult> DeleteIndicatorAsync(Guid indicatorId, Guid userId, CancellationToken ct = default)
    {
        var indicator = await db.LabIndicators.FirstOrDefaultAsync(i => i.Id == indicatorId, ct);
        if (indicator is null) return DeleteIndicatorResult.NotFound;
        if (indicator.OwnerUserId != userId) return DeleteIndicatorResult.Forbidden;

        db.LabIndicators.Remove(indicator);
        await db.SaveChangesAsync(ct);
        return DeleteIndicatorResult.Success;
    }

    private async Task<ExtractionQueryResult> CheckAccessAsync(Guid recordId, Guid userId, CancellationToken ct, bool writeAudit = false)
    {
        var record = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => new { r.Id, r.OwnerUserId }).FirstOrDefaultAsync(ct);
        if (record is null) return ExtractionQueryResult.NotFound;

        if (!await medicalRecords.IsVisibleToAsync(recordId, userId, ct)) return ExtractionQueryResult.Forbidden;

        if (writeAudit && record.OwnerUserId != userId)
            await audit.WriteAsync(userId, MedicalAccessAction.ViewList, ownerUserId: record.OwnerUserId, medicalRecordId: recordId, ct: ct);

        return ExtractionQueryResult.Success;
    }

    private static IndicatorDto ToDto(DomainLabIndicator i) => new(
        i.Id, i.AnalyteKey, i.DisplayName, i.Flag, i.RefSource, i.Specimen, i.Position,
        i.ValueRaw, i.Unit, i.RefLowText, i.RefHighText, i.RefText, i.RecordDate, i.MedicalRecordId, i.SpecimenCustomId);

    private static double? ParseNumeric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
