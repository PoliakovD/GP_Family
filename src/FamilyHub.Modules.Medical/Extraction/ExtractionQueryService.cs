using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Persistence;
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
public class ExtractionQueryService(AppDbContext db, MedicalRecordService medicalRecords, IMedicalAuditWriter audit)
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
            job.Status, job.Stage, job.IndicatorCount, job.Error, job.CreatedAt, job.CompletedAt));
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
    /// GetSummaryAsync для показателей анализа (Kind=Analysis использует SummaryJson, не это поле).</summary>
    public async Task<(ExtractionQueryResult Result, VisitConclusion? Item)> GetConclusionAsync(
        Guid recordId, Guid userId, CancellationToken ct = default)
    {
        var access = await CheckAccessAsync(recordId, userId, ct, writeAudit: true);
        if (access != ExtractionQueryResult.Success) return (access, null);

        var extractedDataJson = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => r.ExtractedDataJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(extractedDataJson)) return (ExtractionQueryResult.NotFound, null);

        var conclusion = JsonSerializer.Deserialize<VisitConclusion>(extractedDataJson);
        return conclusion is null ? (ExtractionQueryResult.NotFound, null) : (ExtractionQueryResult.Success, conclusion);
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

    /// <summary>Последнее значение по каждому показателю среди СВОИХ записей пользователя
    /// (владелец) — расшаренные чужие записи сюда не входят, "мои показатели" в буквальном смысле.</summary>
    public async Task<List<MyIndicatorSummary>> GetMyIndicatorsAsync(Guid userId, CancellationToken ct = default)
    {
        var all = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId)
            .ToListAsync(ct);

        return all
            .GroupBy(i => i.AnalyteKey)
            .Select(g => g.OrderByDescending(i => i.RecordDate).First())
            .Select(i => new MyIndicatorSummary(i.AnalyteKey, i.DisplayName, i.ValueRaw, i.Unit, i.Flag, i.RecordDate))
            .OrderBy(s => s.DisplayName)
            .ToList();
    }

    public async Task<List<IndicatorHistoryPoint>> GetHistoryAsync(Guid userId, string analyteKey, CancellationToken ct = default)
    {
        var items = await db.LabIndicators.AsNoTracking()
            .Where(i => i.OwnerUserId == userId && i.AnalyteKey == analyteKey)
            .OrderBy(i => i.RecordDate)
            .ToListAsync(ct);

        return items.Select(i => new IndicatorHistoryPoint(i.RecordDate, i.ValueRaw, i.ValueNumericText, i.Flag, i.MedicalRecordId)).ToList();
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
        i.Id, i.AnalyteKey, i.DisplayName, i.Flag, i.Position,
        i.ValueRaw, i.Unit, i.RefLowText, i.RefHighText, i.RefText, i.RecordDate, i.MedicalRecordId);
}
