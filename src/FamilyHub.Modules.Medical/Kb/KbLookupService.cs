using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Каскадный поиск препарата в общем справочнике (этап 4): точное совпадение → торговое
/// название (алиас) → нечёткое совпадение (триграммы + tsvector). Как и SearchService.SearchKbAsync,
/// работает raw SQL — search_vector и Aliases намеренно вне EF-модели (см. миграцию AddMedicationEnrichment).
/// Пороги увереннее общего поиска (там 0.3): ошибочная автопривязка в медицинском справочнике
/// дороже промаха, поэтому средняя полоса уверенности возвращается как кандидат, а не как хит.
/// </summary>
public class KbLookupService(AppDbContext db)
{
    /// <summary>От этого порога и выше — уверенная автоматическая привязка.</summary>
    private const double AutoLinkConfidence = 0.55;

    /// <summary>От этого порога до AutoLinkConfidence — кандидат, показываем пользователю, не привязываем сами.</summary>
    private const double CandidateConfidence = 0.35;

    /// <summary>Тот же порог, что и pg_trgm.similarity_threshold (см. RussianTextSearcher) — ниже него
    /// в выборку кандидатов на ранжирование не берём вовсе.</summary>
    private const double TrigramFloor = 0.3;

    public async Task<KbLookupResult> LookupAsync(string normalizedName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return KbLookupResult.Miss;

        var exact = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_medications_kb
            WHERE "NormalizedName" = {normalizedName}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (exact is not null) return KbLookupResult.Hit(exact.Id, exact.DisplayName, exact.Score);

        var alias = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_medications_kb
            WHERE {normalizedName} = ANY("Aliases")
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (alias is not null) return KbLookupResult.Hit(alias.Id, alias.DisplayName, alias.Score);

        var fuzzy = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName",
                   GREATEST(
                       similarity("NormalizedName", {normalizedName}),
                       similarity("DisplayName", {normalizedName})
                   ) AS "Score"
            FROM kb.global_medications_kb
            WHERE search_vector @@ plainto_tsquery('russian', {normalizedName})
               OR similarity("NormalizedName", {normalizedName}) > {TrigramFloor}
               OR similarity("DisplayName", {normalizedName}) > {TrigramFloor}
            ORDER BY "Score" DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

        if (fuzzy is null) return KbLookupResult.Miss;
        if (fuzzy.Score >= AutoLinkConfidence) return KbLookupResult.Hit(fuzzy.Id, fuzzy.DisplayName, fuzzy.Score);
        if (fuzzy.Score >= CandidateConfidence) return KbLookupResult.Candidate(fuzzy.Id, fuzzy.DisplayName, fuzzy.Score);
        return KbLookupResult.Miss;
    }
}
