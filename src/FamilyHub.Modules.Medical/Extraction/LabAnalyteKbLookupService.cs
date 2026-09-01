using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Каскадный поиск показателя в kb.global_lab_analytes_kb (ветка medicalrecords) — точная копия
/// логики <see cref="KbLookupService"/> (этап 4) на другую таблицу, но с дополнительным измерением:
/// ключ справочника теперь (показатель, биоматериал) (пересборка enrich-пайплайна), поэтому каждый
/// шаг каскада (точное совпадение → алиас → нечёткое) сначала ищет запись под конкретный
/// specimen, и только при промахе — среди записей с Specimen=Unknown (обобщённое знание,
/// накопленное до того, как документ определял биоматериал, или когда сам документ его не дал).
/// Специфичная запись всегда предпочтительнее обобщённой — фолбэк применяется только когда прямой
/// поиск под specimen ничего не нашёл. Пороги те же — ошибочная автопривязка референсного
/// диапазона к чужому показателю дороже промаха. Переиспользует <see cref="KbLookupResult"/>/
/// <see cref="KbLookupKind"/> (Kb/) — форма результата уже достаточно общая, заводить второй набор
/// типов ради другой таблицы избыточно.
/// </summary>
public class LabAnalyteKbLookupService(AppDbContext db)
{
    private const double AutoLinkConfidence = 0.55;
    private const double CandidateConfidence = 0.35;
    private const double TrigramFloor = 0.3;

    public async Task<KbLookupResult> LookupAsync(
        string normalizedName, SpecimenType specimen, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return KbLookupResult.Miss;

        var specific = await LookupBySpecimenAsync(normalizedName, (int)specimen, ct);
        if (specific.Kind != KbLookupKind.Miss) return specific;

        // Фолбэк на обобщённую запись — только когда под конкретный биоматериал ничего не нашлось,
        // и только если сам specimen не Unknown (иначе это был бы тот же самый запрос дважды).
        if (specimen == SpecimenType.Unknown) return KbLookupResult.Miss;
        return await LookupBySpecimenAsync(normalizedName, (int)SpecimenType.Unknown, ct);
    }

    private async Task<KbLookupResult> LookupBySpecimenAsync(string normalizedName, int specimenValue, CancellationToken ct)
    {
        var exact = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_lab_analytes_kb
            WHERE "NormalizedName" = {normalizedName} AND "Specimen" = {specimenValue}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (exact is not null) return KbLookupResult.Hit(exact.Id, exact.DisplayName, exact.Score);

        var alias = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_lab_analytes_kb
            WHERE {normalizedName} = ANY("Aliases") AND "Specimen" = {specimenValue}
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
            WHERE "Specimen" = {specimenValue}
              AND (search_vector @@ plainto_tsquery('russian', {normalizedName})
               OR similarity("NormalizedName", {normalizedName}) > {TrigramFloor}
               OR similarity("DisplayName", {normalizedName}) > {TrigramFloor})
            ORDER BY "Score" DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

        if (fuzzy is null) return KbLookupResult.Miss;
        if (fuzzy.Score >= AutoLinkConfidence) return KbLookupResult.Hit(fuzzy.Id, fuzzy.DisplayName, fuzzy.Score);
        if (fuzzy.Score >= CandidateConfidence) return KbLookupResult.Candidate(fuzzy.Id, fuzzy.DisplayName, fuzzy.Score);
        return KbLookupResult.Miss;
    }
}
