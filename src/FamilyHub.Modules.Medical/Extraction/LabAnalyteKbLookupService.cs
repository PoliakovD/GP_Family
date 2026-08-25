using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Каскадный поиск показателя в kb.global_lab_analytes_kb (ветка medicalrecords) — точная копия
/// логики <see cref="KbLookupService"/> (этап 4) на другую таблицу: точное совпадение → алиас
/// ("Hb"/"HGB" → "гемоглобин") → нечёткое (триграммы + tsvector). Пороги те же — ошибочная
/// автопривязка референсного диапазона к чужому показателю дороже промаха. Переиспользует
/// <see cref="KbLookupResult"/>/<see cref="KbLookupKind"/> (Kb/) — форма результата уже
/// достаточно общая, заводить второй набор типов ради другой таблицы избыточно.
/// </summary>
public class LabAnalyteKbLookupService(AppDbContext db)
{
    private const double AutoLinkConfidence = 0.55;
    private const double CandidateConfidence = 0.35;
    private const double TrigramFloor = 0.3;

    public async Task<KbLookupResult> LookupAsync(string normalizedName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return KbLookupResult.Miss;

        var exact = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_lab_analytes_kb
            WHERE "NormalizedName" = {normalizedName}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (exact is not null) return KbLookupResult.Hit(exact.Id, exact.DisplayName, exact.Score);

        var alias = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_lab_analytes_kb
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
            FROM kb.global_lab_analytes_kb
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
