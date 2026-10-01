using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Единственная точка записи в kb.global_lab_analytes_kb (ветка medicalrecords) — зеркало
/// KbWriter (этап 4) на другую таблицу. Изоляция справочника (задача 2.6) — тем же общим
/// <see cref="KbIsolationGuard"/>, что и у KbWriter: структурно (GlobalLabAnalyteKb не имеет
/// полей под персональный контекст) и на уровне значений.
/// Upsert по (NormalizedName, SpecimenKbId) — raw SQL, как и весь остальной доступ к kb (см.
/// LabAnalyteKbLookupService): Aliases/search_vector вне EF-модели.
///
/// Ручная правка справочника после ИИ (§3 плана) — LockedFields ("displayName"/"payload"/
/// "aliases", см. AdminCatalogEndpoints) защищает поле от следующего апсерта: залоченное поле
/// в ON CONFLICT DO UPDATE остаётся старым, остальные обновляются как раньше. Гранулярность —
/// на уровне поля/всего payload, не отдельных ключей внутри jsonb: админ правит и видит
/// PayloadJson целиком (как редактор промптов — сырой текст, не форма по каждому подполю),
/// это и проще для реализации, и честнее — переобогащение всё равно пишет payload целиком,
/// частичный лок отдельных ключей создавал бы иллюзию точности там, где её нет.
/// </summary>
public class LabAnalyteKbWriter(AppDbContext db, KbChangeLogService changeLog, ILogger<LabAnalyteKbWriter> logger)
{
    /// <summary>Нормализованные алиасы записи (без самого ключа) — общий с очередью «Одобрение»
    /// (ADR-0018), чтобы черновик показывал ровно те алиасы, что лягут в kb.</summary>
    public static string[] BuildAliases(string normalizedName, LabAnalyteSummary summary) =>
        summary.Aliases
            .Select(LabAnalyteNormalizer.NormalizeAnalyteKey)
            .Where(a => a.Length > 0 && a != normalizedName)
            .Distinct()
            .ToArray();

    /// <param name="actor">Кто пишет — в журнал изменений (ADR-0018): "system" — автообогащение, "admin" — одобрение из очереди.</param>
    public async Task<KbWriteResult> UpsertAsync(
        string normalizedName, Guid specimenKbId, string rawDisplayName, LabAnalyteSummary summary,
        string source, CancellationToken ct = default,
        string actor = KbChangeLogService.ActorSystem, string? logNote = null)
    {
        // Каноническое имя справочника — очищенное (без нумерации пункта бланка, без КАПС), не
        // сырое SourceDisplayName задачи (пересборка enrich-пайплайна): это единственный писатель
        // DisplayName в kb.global_lab_analytes_kb, поэтому чистка идёт здесь, в одном месте.
        var displayName = LabAnalyteNameCleaner.Clean(rawDisplayName);
        var violation = FindViolation(displayName, summary);
        if (violation is not null)
        {
            logger.LogWarning(
                "Запись в справочник показателей «{DisplayName}» отклонена: подозрение на персональный контекст ({Violation}).",
                displayName, violation);
            return KbWriteResult.Rejected($"Payload содержит подозрение на персональный контекст: {violation}");
        }

        var payloadJson = LabAnalyteKbPayload.Build(summary);

        // Гранулярные локи подполей (§4 плана) — LockedFields может нести "payload.<key>" помимо
        // "payload" целиком (см. KbPayloadLockMerger); лишнее чтение на upsert, но обогащение и
        // так уже сделало платный веб-запрос выше по цепочке — не узкое место.
        var before = await KbRowStore.ReadLabAnalyteAsync(db, normalizedName, specimenKbId, ct);
        if (before is not null && !before.LockedFields.Contains("payload"))
        {
            var lockedKeys = KbPayloadLockMerger.ExtractLockedPayloadKeys(before.LockedFields);
            if (lockedKeys.Count > 0)
                payloadJson = KbPayloadLockMerger.MergeLockedKeys(before.PayloadJson, payloadJson, lockedKeys);
        }

        // Проверка человеком (ADR-0018) сбрасывается, если итоговый payload отличается от проверенного.
        var keepVerification = KbRowStore.KeepVerification(before, payloadJson);

        var aliases = BuildAliases(normalizedName, summary);

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Ручная правка справочника (§3 плана) — залоченные поля переживают переобогащение:
        // "displayName"/"payload"(+Source+PayloadVersion, одна смысловая единица)/"aliases"
        // остаются старыми, если админ их залочил (AdminCatalogEndpoints), остальные обновляются
        // как раньше. LockedFields пуст для подавляющего большинства строк — CASE вырождается в
        // прежнее безусловное присвоение EXCLUDED.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.global_lab_analytes_kb
                ("Id", "NormalizedName", "SpecimenKbId", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "LockedFields", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {normalizedName}, {specimenKbId}, {displayName}, {payloadJson}::jsonb, {LabAnalyteSummarySchema.CurrentVersion}, {source}, {aliases}, {Array.Empty<string>()}, {now}, {now})
            ON CONFLICT ("NormalizedName", "SpecimenKbId") DO UPDATE SET
                "DisplayName" = CASE WHEN 'displayName' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."DisplayName" ELSE EXCLUDED."DisplayName" END,
                "PayloadJson" = CASE WHEN 'payload' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."PayloadJson" ELSE EXCLUDED."PayloadJson" END,
                "PayloadVersion" = CASE WHEN 'payload' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."PayloadVersion" ELSE EXCLUDED."PayloadVersion" END,
                "Source" = CASE WHEN 'payload' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."Source" ELSE EXCLUDED."Source" END,
                "Aliases" = CASE WHEN 'aliases' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."Aliases"
                    ELSE ARRAY(SELECT DISTINCT unnest(kb.global_lab_analytes_kb."Aliases" || EXCLUDED."Aliases")) END,
                "VerificationStatus" = CASE WHEN {keepVerification} OR 'payload' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."VerificationStatus" ELSE 0 END,
                "VerifiedAt" = CASE WHEN {keepVerification} OR 'payload' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."VerifiedAt" ELSE NULL END,
                "VerifiedPayloadHash" = CASE WHEN {keepVerification} OR 'payload' = ANY(kb.global_lab_analytes_kb."LockedFields")
                    THEN kb.global_lab_analytes_kb."VerifiedPayloadHash" ELSE NULL END,
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        var after = await KbRowStore.ReadLabAnalyteAsync(db, normalizedName, specimenKbId, ct)
            ?? throw new InvalidOperationException("Запись справочника не найдена сразу после upsert.");
        var actualId = after.Id;

        if (KbRowStore.Differs(before, after))
        {
            await changeLog.RecordAsync(
                KbChangeTarget.LabAnalyteKb, actualId, after.DisplayName, "ai-write",
                KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), actor, logNote, ct: ct);
        }

        logger.LogInformation(
            "Справочник показателей пополнен: «{DisplayName}» ({NormalizedName}, {SpecimenKbId}), источник: {Source}.",
            displayName, normalizedName, specimenKbId, source);
        return KbWriteResult.Ok(actualId);
    }

    private static string? FindViolation(string displayName, LabAnalyteSummary summary)
    {
        var candidates = new List<string?>
        {
            displayName, summary.LoincCode, summary.DefaultUnit, summary.PlainExplanation,
            summary.WhyMeasured, summary.HighMeans, summary.LowMeans,
        };
        candidates.AddRange(summary.Aliases);

        return KbIsolationGuard.FindViolation(candidates);
    }
}
