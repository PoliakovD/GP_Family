using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Jobs;

/// <summary>Одна строка глобального индикатора фоновых процессов (§4 плана «живой конвейер») —
/// RecordId/RecordKind null, когда цель уже не существует (запись/медикамент удалены к моменту
/// опроса — все четыре таблицы задач хранят такие ссылки справочно, не как FK) — тогда строка
/// в выпадающем списке остаётся просто текстом, без навигации.</summary>
public record ActiveJobItem(Guid JobId, string Label, Guid? RecordId, NotificationRelatedKind? RecordKind, DateTime CreatedAt);

/// <summary>Total — реальный COUNT (для бейджа), Items — top-N старейших (для выпадающего списка,
/// не грузим сотни строк ради индикатора).</summary>
public record ActiveJobsGroup(int Total, IReadOnlyList<ActiveJobItem> Items);

public record ActiveJobsSummaryResponse(
    ActiveJobsGroup Extraction, ActiveJobsGroup LabAnalyte, ActiveJobsGroup Medication, ActiveJobsGroup VisitMedication);

/// <summary>
/// Пользовательский, урезанный аналог AdminPipelineEndpoints.MapGet("/jobs") (§4 плана) — не
/// список всех задач конвейера, а только СВОИ активные (Pending/Running), сгруппированные по
/// одному из четырёх конвейеров. Живёт в хосте (FamilyHub.Api), не в модуле, по той же причине,
/// что HomeSummaryService — RequestedByUserId есть на всех четырёх таблицах задач Medical, но
/// сама Medical не должна знать об этом пользовательском агрегате.
/// </summary>
public class UserJobsService(AppDbContext db)
{
    /// <summary>Top-N в выпадающем списке — Total (COUNT) отдельно показывает, что реально задач больше.</summary>
    private const int MaxItemsPerGroup = 5;

    public async Task<ActiveJobsSummaryResponse> BuildAsync(Guid userId, CancellationToken ct = default)
    {
        var extraction = await BuildExtractionGroupAsync(userId, ct);
        var labAnalyte = await BuildLabAnalyteGroupAsync(userId, ct);
        var medication = await BuildMedicationGroupAsync(userId, ct);
        var visitMedication = await BuildVisitMedicationGroupAsync(userId, ct);
        return new ActiveJobsSummaryResponse(extraction, labAnalyte, medication, visitMedication);
    }

    private async Task<ActiveJobsGroup> BuildExtractionGroupAsync(Guid userId, CancellationToken ct)
    {
        var query = db.MedicalDocumentExtractionJobs.AsNoTracking()
            .Where(j => j.RequestedByUserId == userId
                && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(j => j.CreatedAt).Take(MaxItemsPerGroup)
            .Select(j => new { j.Id, j.MedicalRecordId, j.CreatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return new ActiveJobsGroup(total, []);

        var recordIds = rows.Select(r => r.MedicalRecordId).ToList();
        var records = await db.MedicalRecords.AsNoTracking()
            .Where(r => recordIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Title, r.Kind })
            .ToDictionaryAsync(r => r.Id, ct);

        var items = rows.Select(r =>
        {
            if (!records.TryGetValue(r.MedicalRecordId, out var mr))
                return new ActiveJobItem(r.Id, "Медицинская запись", null, null, r.CreatedAt);

            var isVisit = mr.Kind == MedicalRecordKind.DoctorVisit;
            var label = mr.Title ?? (isVisit ? "Приём врача" : "Анализ");
            var kind = isVisit ? NotificationRelatedKind.MedicalRecordVisit : NotificationRelatedKind.MedicalRecordAnalysis;
            return new ActiveJobItem(r.Id, label, mr.Id, kind, r.CreatedAt);
        }).ToList();

        return new ActiveJobsGroup(total, items);
    }

    private async Task<ActiveJobsGroup> BuildLabAnalyteGroupAsync(Guid userId, CancellationToken ct)
    {
        var query = db.LabAnalyteEnrichmentJobs.AsNoTracking()
            .Where(j => j.RequestedByUserId == userId
                && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(j => j.CreatedAt).Take(MaxItemsPerGroup)
            .Select(j => new { j.Id, j.SourceDisplayName, j.LabIndicatorId, j.CreatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return new ActiveJobsGroup(total, []);

        // LabIndicatorId — справочно (см. класс-doc LabAnalyteEnrichmentJob): показатель мог
        // исчезнуть (запись удалена, показатель перезаписан следующим прогоном «Распознать»
        // на другой ключ). Резолвим MedicalRecordId только по ещё существующим показателям —
        // остальные строки просто без навигации.
        var indicatorIds = rows.Where(r => r.LabIndicatorId is not null).Select(r => r.LabIndicatorId!.Value).ToList();
        var recordByIndicator = indicatorIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.LabIndicators.AsNoTracking()
                .Where(i => indicatorIds.Contains(i.Id))
                .Select(i => new { i.Id, i.MedicalRecordId })
                .ToDictionaryAsync(i => i.Id, i => i.MedicalRecordId, ct);

        var items = rows.Select(r =>
        {
            Guid? recordId = r.LabIndicatorId is not null && recordByIndicator.TryGetValue(r.LabIndicatorId.Value, out var rid)
                ? rid : null;
            // Показатели живут только на записях-анализах (Kind=Analysis) — заключения врача
            // (Kind=DoctorVisit) хранят PrescribedMedications, не LabIndicators.
            return new ActiveJobItem(
                r.Id, r.SourceDisplayName, recordId, recordId is null ? null : NotificationRelatedKind.MedicalRecordAnalysis, r.CreatedAt);
        }).ToList();

        return new ActiveJobsGroup(total, items);
    }

    private async Task<ActiveJobsGroup> BuildMedicationGroupAsync(Guid userId, CancellationToken ct)
    {
        var query = db.MedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.RequestedByUserId == userId
                && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(j => j.CreatedAt).Take(MaxItemsPerGroup)
            .Select(j => new { j.Id, j.SourceDisplayName, j.MedicationId, j.CreatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return new ActiveJobsGroup(total, []);

        // MedicationId — справочно (см. класс-doc MedicationEnrichmentJob): медикамент мог быть
        // удалён до завершения обогащения. Навигация ведёт на аптечку (MedkitId), не на сам
        // медикамент — экран открытой аптечки один на все её медикаменты.
        var medicationIds = rows.Where(r => r.MedicationId is not null).Select(r => r.MedicationId!.Value).ToList();
        var medkitByMedication = medicationIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Set<Medication>().AsNoTracking()
                .Where(m => medicationIds.Contains(m.Id))
                .Select(m => new { m.Id, m.MedkitId })
                .ToDictionaryAsync(m => m.Id, m => m.MedkitId, ct);

        var items = rows.Select(r =>
        {
            Guid? medkitId = r.MedicationId is not null && medkitByMedication.TryGetValue(r.MedicationId.Value, out var mk)
                ? mk : null;
            return new ActiveJobItem(
                r.Id, r.SourceDisplayName, medkitId, medkitId is null ? null : NotificationRelatedKind.Medkit, r.CreatedAt);
        }).ToList();

        return new ActiveJobsGroup(total, items);
    }

    private async Task<ActiveJobsGroup> BuildVisitMedicationGroupAsync(Guid userId, CancellationToken ct)
    {
        var query = db.VisitMedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.RequestedByUserId == userId
                && (j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(j => j.CreatedAt).Take(MaxItemsPerGroup)
            .Select(j => new { j.Id, j.SourceDisplayName, j.MedicalRecordId, j.CreatedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return new ActiveJobsGroup(total, []);

        // MedicalRecordId — справочно, как и MedicationId выше; проверяем существование записи,
        // а не доверяем ссылке молча (та же защита от битой навигации, что у LabAnalyte/Medication).
        var recordIds = rows.Where(r => r.MedicalRecordId is not null).Select(r => r.MedicalRecordId!.Value).ToList();
        var existingRecordIds = recordIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.MedicalRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct)).ToHashSet();

        var items = rows.Select(r =>
        {
            // Заключения врача — единственный источник этого конвейера, запись всегда DoctorVisit.
            Guid? recordId = r.MedicalRecordId is not null && existingRecordIds.Contains(r.MedicalRecordId.Value)
                ? r.MedicalRecordId : null;
            return new ActiveJobItem(
                r.Id, r.SourceDisplayName, recordId, recordId is null ? null : NotificationRelatedKind.MedicalRecordVisit, r.CreatedAt);
        }).ToList();

        return new ActiveJobsGroup(total, items);
    }
}
