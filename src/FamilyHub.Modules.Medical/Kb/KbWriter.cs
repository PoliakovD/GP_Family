using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Единственная точка записи в kb.global_medications_kb (этап 4 — «писатель», которого явно
/// ждёт KbIsolationGuardTests.PersonalContext_CannotBeStoredInKbRow). Инвариант изоляции
/// справочника (задача 2.6) охраняется дважды: структурно (GlobalMedicationKb не имеет полей
/// под персональный контекст — см. KbIsolationGuardTests) и на уровне значений через общий
/// <see cref="KbIsolationGuard"/> (ветка medicalrecords: вынесен отсюда при добавлении второго
/// writer'а — LabAnalyteKbWriter) — на случай, если модель случайно подмешает в текст что-то
/// похожее на идентификатор.
/// Upsert по NormalizedName — raw SQL, как и весь остальной доступ к kb (см. KbLookupService):
/// Aliases/search_vector вне EF-модели.
/// </summary>
public class KbWriter(AppDbContext db, KbChangeLogService changeLog, ILogger<KbWriter> logger)
{
    /// <param name="extraAliases">Доп. алиасы помимо summary.TradeNames — например, исходное
    /// (искажённое OCR) название, когда запись пишется под исправленным именем (см.
    /// MedicationEnrichmentProcessor): следующее распознавание той же опечатки находит запись
    /// сразу через алиас, без повторного внешнего поиска.</param>
    /// <param name="actor">Кто пишет — в журнал изменений (ADR-0018): "system" для автообогащения, "admin" — одобрение из очереди.</param>
    /// <param name="logNote">Заметка к записи журнала.</param>
    public async Task<KbWriteResult> UpsertAsync(
        string normalizedName, string displayName, MedicationSummary summary, string source,
        IReadOnlyList<string>? extraAliases = null, CancellationToken ct = default,
        string actor = KbChangeLogService.ActorSystem, string? logNote = null)
    {
        var violation = FindViolation(displayName, summary, extraAliases);
        if (violation is not null)
        {
            logger.LogWarning(
                "Запись в справочник «{DisplayName}» отклонена: подозрение на персональный контекст ({Violation}).",
                displayName, violation);
            return KbWriteResult.Rejected($"Payload содержит подозрение на персональный контекст: {violation}");
        }

        var payloadJson = BuildPayloadJson(summary);

        // Гранулярные локи подполей (§4 плана) — см. LabAnalyteKbWriter.UpsertAsync, тот же приём
        // на другую таблицу (KbPayloadLockMerger общий для обоих writer'ов).
        var before = await KbRowStore.ReadMedicationAsync(db, normalizedName, ct);
        if (before is not null && !before.LockedFields.Contains("payload"))
        {
            var lockedKeys = KbPayloadLockMerger.ExtractLockedPayloadKeys(before.LockedFields);
            if (lockedKeys.Count > 0)
                payloadJson = KbPayloadLockMerger.MergeLockedKeys(before.PayloadJson, payloadJson, lockedKeys);
        }

        // Проверка человеком (ADR-0018) сбрасывается, если итоговый payload отличается от проверенного.
        var keepVerification = KbRowStore.KeepVerification(before, payloadJson);

        // Алиасы — нормализованные торговые названия (та же функция, что и ключ дедупликации) плюс
        // extraAliases (исходное искажённое OCR название при переименовании, см. параметр выше),
        // без самого NormalizedName (иначе он же попал бы и в основной ключ, и в алиасы).
        var aliases = BuildAliases(normalizedName, summary, extraAliases);

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Ручная правка справочника (§3 плана) — залоченные поля переживают переобогащение, тот
        // же приём, что LabAnalyteKbWriter (см. его class doc для полного объяснения).
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.global_medications_kb
                ("Id", "NormalizedName", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "LockedFields", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {normalizedName}, {displayName}, {payloadJson}::jsonb, {MedicationSummarySchema.CurrentVersion}, {source}, {aliases}, {Array.Empty<string>()}, {now}, {now})
            ON CONFLICT ("NormalizedName") DO UPDATE SET
                "DisplayName" = CASE WHEN 'displayName' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."DisplayName" ELSE EXCLUDED."DisplayName" END,
                "PayloadJson" = CASE WHEN 'payload' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."PayloadJson" ELSE EXCLUDED."PayloadJson" END,
                "PayloadVersion" = CASE WHEN 'payload' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."PayloadVersion" ELSE EXCLUDED."PayloadVersion" END,
                "Source" = CASE WHEN 'payload' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."Source" ELSE EXCLUDED."Source" END,
                "Aliases" = CASE WHEN 'aliases' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."Aliases"
                    ELSE ARRAY(SELECT DISTINCT unnest(kb.global_medications_kb."Aliases" || EXCLUDED."Aliases")) END,
                "VerificationStatus" = CASE WHEN {keepVerification} OR 'payload' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."VerificationStatus" ELSE 0 END,
                "VerifiedAt" = CASE WHEN {keepVerification} OR 'payload' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."VerifiedAt" ELSE NULL END,
                "VerifiedPayloadHash" = CASE WHEN {keepVerification} OR 'payload' = ANY(kb.global_medications_kb."LockedFields")
                    THEN kb.global_medications_kb."VerifiedPayloadHash" ELSE NULL END,
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        // ExecuteSqlInterpolatedAsync не возвращает строки (ON CONFLICT мог вернуть Id уже
        // существующей записи, не сгенерированный выше) — читаем фактический Id отдельным SELECT.
        var after = await KbRowStore.ReadMedicationAsync(db, normalizedName, ct)
            ?? throw new InvalidOperationException("Запись справочника не найдена сразу после upsert.");
        var actualId = after.Id;

        if (KbRowStore.Differs(before, after))
        {
            await changeLog.RecordAsync(
                KbChangeTarget.MedicationKb, actualId, after.DisplayName, "ai-write",
                KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), actor, logNote, ct: ct);
        }

        logger.LogInformation("Справочник пополнен: «{DisplayName}» ({NormalizedName}), источник: {Source}.",
            displayName, normalizedName, source);
        return KbWriteResult.Ok(actualId);
    }

    /// <summary>Форма jsonb-поля PayloadJson — единственное место, которое её знает (его же использует
    /// очередь «Одобрение», ADR-0018, чтобы показать черновик в том виде, в каком он ляжет в kb).</summary>
    public static string BuildPayloadJson(MedicationSummary summary) => JsonSerializer.Serialize(new
    {
        schemaVersion = MedicationSummarySchema.CurrentVersion,
        internationalName = summary.InternationalName,
        tradeNames = summary.TradeNames,
        form = summary.Form,
        purpose = summary.Purpose,
        simplePurpose = summary.SimplePurpose,
        usage = summary.Usage,
        storage = summary.Storage,
        driving = summary.Driving,
        specialNotes = summary.SpecialNotes,
    });

    /// <summary>Нормализованные алиасы записи — торговые названия плюс extraAliases, без самого ключа.</summary>
    public static string[] BuildAliases(string normalizedName, MedicationSummary summary, IReadOnlyList<string>? extraAliases) =>
        summary.TradeNames
            .Concat(extraAliases ?? [])
            .Select(MedicationNameNormalizer.Normalize)
            .Where(a => a.Length > 0 && a != normalizedName)
            .Distinct()
            .ToArray();

    private static string? FindViolation(string displayName, MedicationSummary summary, IReadOnlyList<string>? extraAliases)
    {
        var candidates = new List<string?>
        {
            displayName, summary.InternationalName, summary.Form, summary.Purpose, summary.SimplePurpose, summary.Usage,
            summary.Storage, summary.Driving, summary.SpecialNotes,
        };
        candidates.AddRange(summary.TradeNames);
        if (extraAliases is not null) candidates.AddRange(extraAliases);

        return KbIsolationGuard.FindViolation(candidates);
    }
}
