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
/// Upsert по (NormalizedName, Specimen) — raw SQL, как и весь остальной доступ к kb (см.
/// LabAnalyteKbLookupService): Aliases/search_vector вне EF-модели.
/// </summary>
public class LabAnalyteKbWriter(AppDbContext db, ILogger<LabAnalyteKbWriter> logger)
{
    public async Task<KbWriteResult> UpsertAsync(
        string normalizedName, SpecimenType specimen, string displayName, LabAnalyteSummary summary,
        string source, CancellationToken ct = default)
    {
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
        var specimenValue = (int)specimen;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.global_lab_analytes_kb
                ("Id", "NormalizedName", "Specimen", "DisplayName", "PayloadJson", "PayloadVersion", "Source", "Aliases", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {normalizedName}, {specimenValue}, {displayName}, {payloadJson}::jsonb, {LabAnalyteSummarySchema.CurrentVersion}, {source}, {aliases}, {now}, {now})
            ON CONFLICT ("NormalizedName", "Specimen") DO UPDATE SET
                "DisplayName" = EXCLUDED."DisplayName",
                "PayloadJson" = EXCLUDED."PayloadJson",
                "PayloadVersion" = EXCLUDED."PayloadVersion",
                "Source" = EXCLUDED."Source",
                "Aliases" = ARRAY(SELECT DISTINCT unnest(kb.global_lab_analytes_kb."Aliases" || EXCLUDED."Aliases")),
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        var actualId = await db.Database.SqlQuery<KbIdRow>($"""
            SELECT "Id" FROM kb.global_lab_analytes_kb WHERE "NormalizedName" = {normalizedName} AND "Specimen" = {specimenValue}
            """).Select(r => r.Id).SingleAsync(ct);

        logger.LogInformation(
            "Справочник показателей пополнен: «{DisplayName}» ({NormalizedName}, {Specimen}), источник: {Source}.",
            displayName, normalizedName, specimen, source);
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
