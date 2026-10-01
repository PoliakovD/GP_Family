using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Админская часть каталога, добавленная ADR-0018: статус проверки записей человеком (внутренний маркер,
/// пользователям не отдаётся), админский список с фильтром по статусу и откат изменений по журналу.
/// </summary>
public partial class AdminCatalogService
{
    // ------------------------------------------------------------------ проверка человеком

    /// <summary>Отметить запись проверенной без правок (или присвоить другой статус проверки: например
    /// ManualKnowledge при одобрении черновика, построенного на знании эксперта). Хэш — от текущего payload:
    /// если автообогащение позже его изменит, статус сбросится в AiUnverified (см. писатели kb).</summary>
    public async Task<bool> MarkVerifiedAsync(
        KbChangeTarget target, Guid id, KbVerificationStatus status = KbVerificationStatus.AdminVerified,
        string? logNote = null, CancellationToken ct = default)
    {
        if (target is not (KbChangeTarget.LabAnalyteKb or KbChangeTarget.MedicationKb))
            throw new ArgumentOutOfRangeException(nameof(target), "Проверяются только записи справочников.");

        var lab = target == KbChangeTarget.LabAnalyteKb;
        var before = lab
            ? await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct)
            : await KbRowStore.ReadMedicationByIdAsync(db, id, ct);
        if (before is null) return false;

        var statusValue = (int)status;
        var now = DateTime.UtcNow;
        var hash = KbPayloadHash.Compute(before.PayloadJson);
        if (lab)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_lab_analytes_kb
                SET "VerificationStatus" = {statusValue}, "VerifiedAt" = {now}, "VerifiedPayloadHash" = {hash}
                WHERE "Id" = {id}
                """, ct);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_medications_kb
                SET "VerificationStatus" = {statusValue}, "VerifiedAt" = {now}, "VerifiedPayloadHash" = {hash}
                WHERE "Id" = {id}
                """, ct);
        }

        var after = lab
            ? await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct)
            : await KbRowStore.ReadMedicationByIdAsync(db, id, ct);
        await changeLog.RecordAsync(
            target, id, before.DisplayName, "verify",
            KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), KbChangeLogService.ActorAdmin, logNote, ct: ct);
        return true;
    }

    public async Task<AdminKbVerificationSummary> GetVerificationSummaryAsync(CancellationToken ct = default)
    {
        var lab = await db.Database.SqlQuery<StatusCountRow>($"""
            SELECT "VerificationStatus" AS "Status", count(*)::int AS "Count" FROM kb.global_lab_analytes_kb GROUP BY "VerificationStatus"
            """).ToListAsync(ct);
        var med = await db.Database.SqlQuery<StatusCountRow>($"""
            SELECT "VerificationStatus" AS "Status", count(*)::int AS "Count" FROM kb.global_medications_kb GROUP BY "VerificationStatus"
            """).ToListAsync(ct);
        return new AdminKbVerificationSummary(ToCounts(lab), ToCounts(med));
    }

    private static KbVerificationCounts ToCounts(List<StatusCountRow> rows)
    {
        int Count(KbVerificationStatus s) => rows.Where(r => r.Status == (int)s).Sum(r => r.Count);
        return new KbVerificationCounts(
            rows.Sum(r => r.Count), Count(KbVerificationStatus.AiUnverified), Count(KbVerificationStatus.AdminVerified),
            Count(KbVerificationStatus.AdminEdited), Count(KbVerificationStatus.ManualKnowledge));
    }

    // ------------------------------------------------------------------ админский список

    private const int DefaultTake = 20;
    private const int MaxTake = 50;

    public async Task<AdminKbListResponse> SearchLabAnalytesAsync(
        string? q, AdminKbVerificationFilter filter, int skip, int take, CancellationToken ct = default)
    {
        take = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
        skip = Math.Max(skip, 0);
        var mode = (int)filter;

        var rows = string.IsNullOrWhiteSpace(q)
            ? await db.Database.SqlQuery<AdminKbListRow>($"""
                SELECT a."Id", a."DisplayName", a."SpecimenKbId", s."DisplayName" AS "SpecimenDisplayName", a."PayloadJson",
                       a."VerificationStatus", a."VerifiedPayloadHash", a."UpdatedAt"
                FROM kb.global_lab_analytes_kb a
                LEFT JOIN kb.global_specimens_kb s ON s."Id" = a."SpecimenKbId"
                WHERE ({mode} = 0 OR ({mode} = 1 AND a."VerificationStatus" = 0) OR ({mode} = 2 AND a."VerificationStatus" <> 0))
                ORDER BY a."DisplayName"
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<AdminKbListRow>($"""
                SELECT a."Id", a."DisplayName", a."SpecimenKbId", s."DisplayName" AS "SpecimenDisplayName", a."PayloadJson",
                       a."VerificationStatus", a."VerifiedPayloadHash", a."UpdatedAt"
                FROM kb.global_lab_analytes_kb a
                LEFT JOIN kb.global_specimens_kb s ON s."Id" = a."SpecimenKbId"
                WHERE (a.search_vector @@ plainto_tsquery('russian', {q})
                       OR similarity(a."DisplayName", {q}) > 0.3
                       OR lower({q}) = ANY(a."Aliases"))
                  AND ({mode} = 0 OR ({mode} = 1 AND a."VerificationStatus" = 0) OR ({mode} = 2 AND a."VerificationStatus" <> 0))
                ORDER BY GREATEST(
                    ts_rank(a.search_vector, plainto_tsquery('russian', {q})),
                    similarity(a."DisplayName", {q})
                ) DESC
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct);

        return new AdminKbListResponse(rows.Select(r => ToListItem(r, "plainExplanation")).ToList(), HasMore: rows.Count == take);
    }

    public async Task<AdminKbListResponse> SearchMedicationsAsync(
        string? q, AdminKbVerificationFilter filter, int skip, int take, CancellationToken ct = default)
    {
        take = take <= 0 ? DefaultTake : Math.Min(take, MaxTake);
        skip = Math.Max(skip, 0);
        var mode = (int)filter;

        var rows = string.IsNullOrWhiteSpace(q)
            ? await db.Database.SqlQuery<AdminKbListRow>($"""
                SELECT "Id", "DisplayName", NULL::uuid AS "SpecimenKbId", NULL::text AS "SpecimenDisplayName", "PayloadJson",
                       "VerificationStatus", "VerifiedPayloadHash", "UpdatedAt"
                FROM kb.global_medications_kb
                WHERE ({mode} = 0 OR ({mode} = 1 AND "VerificationStatus" = 0) OR ({mode} = 2 AND "VerificationStatus" <> 0))
                ORDER BY "DisplayName"
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<AdminKbListRow>($"""
                SELECT "Id", "DisplayName", NULL::uuid AS "SpecimenKbId", NULL::text AS "SpecimenDisplayName", "PayloadJson",
                       "VerificationStatus", "VerifiedPayloadHash", "UpdatedAt"
                FROM kb.global_medications_kb
                WHERE (search_vector @@ plainto_tsquery('russian', {q})
                       OR similarity("DisplayName", {q}) > 0.3
                       OR lower({q}) = ANY("Aliases"))
                  AND ({mode} = 0 OR ({mode} = 1 AND "VerificationStatus" = 0) OR ({mode} = 2 AND "VerificationStatus" <> 0))
                ORDER BY GREATEST(
                    ts_rank(search_vector, plainto_tsquery('russian', {q})),
                    similarity("DisplayName", {q})
                ) DESC
                OFFSET {skip} LIMIT {take}
                """).ToListAsync(ct);

        return new AdminKbListResponse(rows.Select(r => ToListItem(r, "purpose")).ToList(), HasMore: rows.Count == take);
    }

    private static AdminKbListItem ToListItem(AdminKbListRow r, string previewKey) => new(
        r.Id, r.DisplayName, r.SpecimenKbId, r.SpecimenDisplayName, ReadPreview(r.PayloadJson, previewKey),
        (KbVerificationStatus)r.VerificationStatus, IsStale(r.VerificationStatus, r.VerifiedPayloadHash, r.PayloadJson), r.UpdatedAt);

    private static string? ReadPreview(string payloadJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ откат по журналу

    /// <summary>Откат одной записи журнала: возвращает строку (справочника или кэша) к снимку «было».
    /// Создание откатывается удалением строки, удаление — её восстановлением. Сам откат тоже пишется в
    /// журнал (и отдельно не откатывается — чтобы не получить цепочку «откат отката»; нужная версия
    /// выбирается из истории).</summary>
    public async Task<AdminRevertResult> RevertAsync(Guid logId, CancellationToken ct = default)
    {
        var log = await changeLog.GetAsync(logId, ct);
        if (log is null) return AdminRevertResult.NotFound;
        if (log.Action == "revert") return AdminRevertResult.CannotRevertRevert;
        if (await changeLog.WasRevertedAsync(logId, ct)) return AdminRevertResult.AlreadyReverted;

        return log.Target switch
        {
            KbChangeTarget.LabAnalyteKb => await RevertKbAsync(log, lab: true, ct),
            KbChangeTarget.MedicationKb => await RevertKbAsync(log, lab: false, ct),
            KbChangeTarget.LabAnalyteSearchCache => await RevertCacheAsync(log, lab: true, ct),
            KbChangeTarget.MedicationSearchCache => await RevertCacheAsync(log, lab: false, ct),
            _ => AdminRevertResult.NothingToRevert,
        };
    }

    private async Task<AdminRevertResult> RevertKbAsync(KbChangeLog log, bool lab, CancellationToken ct)
    {
        var target = lab ? KbChangeTarget.LabAnalyteKb : KbChangeTarget.MedicationKb;
        var snap = log.BeforeJson is null ? null : JsonSerializer.Deserialize<KbRecordSnapshot>(log.BeforeJson, KbChangeLogService.JsonOptions);
        var current = lab
            ? await KbRowStore.ReadLabAnalyteByIdAsync(db, log.TargetId, ct)
            : await KbRowStore.ReadMedicationByIdAsync(db, log.TargetId, ct);

        try
        {
            if (snap is null)
            {
                // Запись была создана этим изменением — откат = удалить.
                if (current is null) return AdminRevertResult.NothingToRevert;
                if (lab) await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM kb.global_lab_analytes_kb WHERE "Id" = {log.TargetId}""", ct);
                else await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM kb.global_medications_kb WHERE "Id" = {log.TargetId}""", ct);
            }
            else if (current is null)
            {
                await RestoreInsertAsync(snap, lab, ct);
            }
            else
            {
                await RestoreUpdateAsync(snap, lab, ct);
            }
        }
        catch (Npgsql.PostgresException)
        {
            // Занят ключ (NormalizedName[, SpecimenKbId]) — на месте удалённой строки уже появилась другая.
            return AdminRevertResult.Conflict;
        }

        var after = snap is null
            ? null
            : lab ? await KbRowStore.ReadLabAnalyteByIdAsync(db, log.TargetId, ct) : await KbRowStore.ReadMedicationByIdAsync(db, log.TargetId, ct);
        await changeLog.RecordAsync(
            target, log.TargetId, snap?.DisplayName ?? current?.DisplayName ?? log.TargetLabel, "revert",
            KbChangeLogService.ToJson(current), KbChangeLogService.ToJson(after), KbChangeLogService.ActorAdmin,
            $"Откат записи журнала от {log.At:dd.MM.yyyy HH:mm}", log.Id, ct);
        return AdminRevertResult.Ok;
    }

    private async Task RestoreUpdateAsync(KbRecordSnapshot s, bool lab, CancellationToken ct)
    {
        var status = (int)s.VerificationStatus;
        var now = DateTime.UtcNow;
        if (lab)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_lab_analytes_kb SET
                    "DisplayName" = {s.DisplayName}, "PayloadJson" = {s.PayloadJson}::jsonb, "Source" = {s.Source},
                    "Aliases" = {s.Aliases}, "LockedFields" = {s.LockedFields}, "PayloadVersion" = {s.PayloadVersion},
                    "VerificationStatus" = {status}, "VerifiedAt" = {s.VerifiedAt}, "VerifiedPayloadHash" = {s.VerifiedPayloadHash},
                    "UpdatedAt" = {now}
                WHERE "Id" = {s.Id}
                """, ct);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE kb.global_medications_kb SET
                    "DisplayName" = {s.DisplayName}, "PayloadJson" = {s.PayloadJson}::jsonb, "Source" = {s.Source},
                    "Aliases" = {s.Aliases}, "LockedFields" = {s.LockedFields}, "PayloadVersion" = {s.PayloadVersion},
                    "VerificationStatus" = {status}, "VerifiedAt" = {s.VerifiedAt}, "VerifiedPayloadHash" = {s.VerifiedPayloadHash},
                    "UpdatedAt" = {now}
                WHERE "Id" = {s.Id}
                """, ct);
        }
    }

    private async Task RestoreInsertAsync(KbRecordSnapshot s, bool lab, CancellationToken ct)
    {
        var status = (int)s.VerificationStatus;
        var now = DateTime.UtcNow;
        if (lab)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO kb.global_lab_analytes_kb
                    ("Id", "NormalizedName", "SpecimenKbId", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "LockedFields",
                     "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "CreatedAt", "UpdatedAt")
                VALUES
                    ({s.Id}, {s.NormalizedName}, {s.SpecimenKbId}, {s.DisplayName}, {s.PayloadJson}::jsonb, {s.PayloadVersion}, {s.Source},
                     {s.Aliases}, {s.LockedFields}, {status}, {s.VerifiedAt}, {s.VerifiedPayloadHash}, {s.CreatedAt}, {now})
                """, ct);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO kb.global_medications_kb
                    ("Id", "NormalizedName", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "LockedFields",
                     "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "CreatedAt", "UpdatedAt")
                VALUES
                    ({s.Id}, {s.NormalizedName}, {s.DisplayName}, {s.PayloadJson}::jsonb, {s.PayloadVersion}, {s.Source},
                     {s.Aliases}, {s.LockedFields}, {status}, {s.VerifiedAt}, {s.VerifiedPayloadHash}, {s.CreatedAt}, {now})
                """, ct);
        }
    }

    private async Task<AdminRevertResult> RevertCacheAsync(KbChangeLog log, bool lab, CancellationToken ct)
    {
        var target = lab ? KbChangeTarget.LabAnalyteSearchCache : KbChangeTarget.MedicationSearchCache;
        var snap = log.BeforeJson is null
            ? null
            : JsonSerializer.Deserialize<SearchCacheSnapshot>(log.BeforeJson, KbChangeLogService.JsonOptions);

        ISearchCacheRow? current = lab
            ? await db.LabAnalyteSearchCaches.FirstOrDefaultAsync(c => c.Id == log.TargetId, ct)
            : await db.MedicationSearchCaches.FirstOrDefaultAsync(c => c.Id == log.TargetId, ct);
        var currentSnapshot = current is null ? null : SearchCacheSnapshots.From(current);

        if (snap is null)
        {
            if (current is null) return AdminRevertResult.NothingToRevert;
            if (lab) db.LabAnalyteSearchCaches.Remove((LabAnalyteSearchCache)current);
            else db.MedicationSearchCaches.Remove((MedicationSearchCache)current);
        }
        else if (current is null)
        {
            if (lab) db.LabAnalyteSearchCaches.Add(SearchCacheSnapshots.ToLabEntity(snap));
            else db.MedicationSearchCaches.Add(SearchCacheSnapshots.ToMedicationEntity(snap));
        }
        else
        {
            SearchCacheSnapshots.Apply(snap, current);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return AdminRevertResult.Conflict;
        }

        await changeLog.RecordAsync(
            target, log.TargetId, snap?.NormalizedName ?? currentSnapshot?.NormalizedName ?? log.TargetLabel, "revert",
            KbChangeLogService.ToJson(currentSnapshot), KbChangeLogService.ToJson(snap), KbChangeLogService.ActorAdmin,
            $"Откат записи журнала от {log.At:dd.MM.yyyy HH:mm}", log.Id, ct);
        return AdminRevertResult.Ok;
    }

    private sealed class StatusCountRow
    {
        public int Status { get; set; }
        public int Count { get; set; }
    }

    private sealed class AdminKbListRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public Guid? SpecimenKbId { get; set; }
        public string? SpecimenDisplayName { get; set; }
        public string PayloadJson { get; set; } = "{}";
        public int VerificationStatus { get; set; }
        public string? VerifiedPayloadHash { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}

/// <summary>Преобразования строки кэша поиска ↔ снимок для журнала/отката (ADR-0018).</summary>
internal static class SearchCacheSnapshots
{
    public static SearchCacheSnapshot From(ISearchCacheRow row) => new(
        row.Id, row.NormalizedName, row is LabAnalyteSearchCache lab ? lab.SpecimenKbId : null, row.Provider,
        row.LastUpdatedAt, row.CanBeUpdatedAfter, row.SnippetsJson, row.OverridesJson);

    public static void Apply(SearchCacheSnapshot snap, ISearchCacheRow row)
    {
        row.Provider = snap.Provider;
        row.LastUpdatedAt = snap.LastUpdatedAt;
        row.CanBeUpdatedAfter = snap.CanBeUpdatedAfter;
        row.SnippetsJson = snap.SnippetsJson;
        row.OverridesJson = snap.OverridesJson;
    }

    public static LabAnalyteSearchCache ToLabEntity(SearchCacheSnapshot s) => new()
    {
        Id = s.Id, NormalizedName = s.NormalizedName, SpecimenKbId = s.SpecimenKbId ?? SpecimenContextIds.Unresolved,
        Provider = s.Provider, LastUpdatedAt = s.LastUpdatedAt, CanBeUpdatedAfter = s.CanBeUpdatedAfter,
        SnippetsJson = s.SnippetsJson, OverridesJson = s.OverridesJson,
    };

    public static MedicationSearchCache ToMedicationEntity(SearchCacheSnapshot s) => new()
    {
        Id = s.Id, NormalizedName = s.NormalizedName, Provider = s.Provider, LastUpdatedAt = s.LastUpdatedAt,
        CanBeUpdatedAfter = s.CanBeUpdatedAfter, SnippetsJson = s.SnippetsJson, OverridesJson = s.OverridesJson,
    };
}
