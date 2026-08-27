using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Каскадный поиск препарата в общем справочнике (этап 4): точное совпадение → торговое
/// название (алиас) → нечёткое совпадение (триграммы + tsvector). Как и SearchService.SearchKbAsync,
/// работает raw SQL — search_vector и Aliases намеренно вне EF-модели (см. миграцию AddMedicationEnrichment).
/// Пороги увереннее общего поиска (там 0.3): ошибочная автопривязка в медицинском справочнике
/// дороже промаха, поэтому средняя полоса уверенности возвращается как кандидат, а не как хит.
///
/// Намеренно БЕЗ кэша: несколько вызывающих (MedicationKbStatusService.BuildStatusAsync — фронт
/// поллит его каждые ~300мс в ожидании результата фонового обогащения;
/// MedicationEnrichmentProcessor.RunAsync — проверяет "не наполнил ли справочник уже сосед" прямо
/// перед платным запросом) специально читают АКТУАЛЬНОЕ состояние прямо сейчас — TTL-кэш здесь
/// один раз уже незаметно ломал именно это (первый промах "залипал" на весь TTL, статус переставал
/// когда-либо доходить до Ready). Вместо кэша — LookupExactManyAsync ниже: батч точного совпадения
/// на N названий разом, БЕЗ хранения между вызовами — свежесть сохраняется, круглые поездки к БД
/// сокращаются только за счёт объединения в одном запросе.
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

    /// <summary>
    /// Батч точного совпадения — один SQL-запрос на ВСЕ уникальные названия сразу, вместо
    /// последовательных вызовов LookupAsync по одному на каждое (аудит, находка High #1):
    /// заключение врача с несколькими препаратами делало до 3 round-trip'ов на КАЖДОЕ название на
    /// каждый просмотр экрана (см. ExtractionQueryService.GetConclusionAsync). Покрывает самый
    /// частый случай — препарат уже в справочнике под тем же нормализованным именем; для
    /// алиасов/нечёткого совпадения вызывающий код падает обратно на LookupAsync поштучно (тот же
    /// код, что и раньше, без изменений — переписывать каскад алиас/нечёткое совпадение в батч не
    /// оправдано этой находкой). Без хранения между вызовами — не кэш, просто объединение запроса.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, KbLookupResult>> LookupExactManyAsync(
        IReadOnlyCollection<string> normalizedNames, CancellationToken ct = default)
    {
        var distinct = normalizedNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
        var result = new Dictionary<string, KbLookupResult>();
        if (distinct.Length == 0) return result;

        var rows = await db.Database.SqlQuery<KbExactBatchRow>($"""
            SELECT "NormalizedName" AS "MatchedName", "Id", "DisplayName"
            FROM kb.global_medications_kb
            WHERE "NormalizedName" = ANY({distinct})
            """).ToListAsync(ct);

        foreach (var row in rows)
            result[row.MatchedName] = KbLookupResult.Hit(row.Id, row.DisplayName, 1.0);

        return result;
    }

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
