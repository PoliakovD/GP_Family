using System.Text.Json;
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
public class KbWriter(AppDbContext db, ILogger<KbWriter> logger)
{
    /// <param name="extraAliases">Доп. алиасы помимо summary.TradeNames — например, исходное
    /// (искажённое OCR) название, когда запись пишется под исправленным именем (см.
    /// MedicationEnrichmentProcessor): следующее распознавание той же опечатки находит запись
    /// сразу через алиас, без повторного внешнего поиска.</param>
    public async Task<KbWriteResult> UpsertAsync(
        string normalizedName, string displayName, MedicationSummary summary, string source,
        IReadOnlyList<string>? extraAliases = null, CancellationToken ct = default)
    {
        var violation = FindViolation(displayName, summary, extraAliases);
        if (violation is not null)
        {
            logger.LogWarning(
                "Запись в справочник «{DisplayName}» отклонена: подозрение на персональный контекст ({Violation}).",
                displayName, violation);
            return KbWriteResult.Rejected($"Payload содержит подозрение на персональный контекст: {violation}");
        }

        var payloadJson = JsonSerializer.Serialize(new
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

        // Алиасы — нормализованные торговые названия (та же функция, что и ключ дедупликации) плюс
        // extraAliases (исходное искажённое OCR название при переименовании, см. параметр выше),
        // без самого NormalizedName (иначе он же попал бы и в основной ключ, и в алиасы).
        var aliases = summary.TradeNames
            .Concat(extraAliases ?? [])
            .Select(MedicationNameNormalizer.Normalize)
            .Where(a => a.Length > 0 && a != normalizedName)
            .Distinct()
            .ToArray();

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
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        // ExecuteSqlInterpolatedAsync не возвращает строки (ON CONFLICT мог вернуть Id уже
        // существующей записи, не сгенерированный выше) — читаем фактический Id отдельным SELECT.
        var actualId = await db.Database.SqlQuery<KbIdRow>($"""
            SELECT "Id" FROM kb.global_medications_kb WHERE "NormalizedName" = {normalizedName}
            """).Select(r => r.Id).SingleAsync(ct);

        logger.LogInformation("Справочник пополнен: «{DisplayName}» ({NormalizedName}), источник: {Source}.",
            displayName, normalizedName, source);
        return KbWriteResult.Ok(actualId);
    }

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
