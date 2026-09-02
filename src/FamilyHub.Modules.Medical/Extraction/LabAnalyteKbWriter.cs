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
/// </summary>
public class LabAnalyteKbWriter(AppDbContext db, ILogger<LabAnalyteKbWriter> logger)
{
    public async Task<KbWriteResult> UpsertAsync(
        string normalizedName, Guid specimenKbId, string rawDisplayName, LabAnalyteSummary summary,
        string source, CancellationToken ct = default)
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

        var aliases = summary.Aliases
            .Select(LabAnalyteNormalizer.Normalize)
            .Where(a => a.Length > 0 && a != normalizedName)
            .Distinct()
            .ToArray();

        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.global_lab_analytes_kb
                ("Id", "NormalizedName", "SpecimenKbId", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {normalizedName}, {specimenKbId}, {displayName}, {payloadJson}::jsonb, {LabAnalyteSummarySchema.CurrentVersion}, {source}, {aliases}, {now}, {now})
            ON CONFLICT ("NormalizedName", "SpecimenKbId") DO UPDATE SET
                "DisplayName" = EXCLUDED."DisplayName",
                "PayloadJson" = EXCLUDED."PayloadJson",
                "PayloadVersion" = EXCLUDED."PayloadVersion",
                "Source" = EXCLUDED."Source",
                "Aliases" = ARRAY(SELECT DISTINCT unnest(kb.global_lab_analytes_kb."Aliases" || EXCLUDED."Aliases")),
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        var actualId = await db.Database.SqlQuery<KbIdRow>($"""
            SELECT "Id" FROM kb.global_lab_analytes_kb WHERE "NormalizedName" = {normalizedName} AND "SpecimenKbId" = {specimenKbId}
            """).Select(r => r.Id).SingleAsync(ct);

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
