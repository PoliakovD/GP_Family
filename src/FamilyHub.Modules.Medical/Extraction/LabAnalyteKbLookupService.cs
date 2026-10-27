using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Каскадный поиск показателя в kb.global_lab_analytes_kb (ветка medicalrecords) — точная копия
/// логики <see cref="KbLookupService"/> (этап 4) на другую таблицу, но с дополнительным измерением:
/// ключ справочника теперь (показатель, источник) (пересборка enrich-пайплайна), поэтому каждый
/// шаг каскада (точное совпадение → алиас → нечёткое) сначала ищет запись под конкретный
/// SpecimenKbId, и только при промахе — среди записей с SpecimenKbId=SpecimenContextIds.Unresolved
/// (обобщённое знание, накопленное до того, как источник определился, или заведённое вручную из
/// админки как общий фолбэк). Специфичная запись всегда предпочтительнее обобщённой — фолбэк
/// применяется только когда прямой поиск под источник ничего не нашёл. Пороги те же — ошибочная
/// автопривязка референсного диапазона к чужому показателю дороже промаха. Переиспользует
/// <see cref="KbLookupResult"/>/<see cref="KbLookupKind"/> (Kb/) — форма результата уже достаточно
/// общая, заводить второй набор типов ради другой таблицы избыточно.
///
/// LOINC-названия протоколов медорганизаций («Мочевина, молярная концентрация в сыворотке или плазме
/// крови») сравниваются по голове (<see cref="LabAnalyteNormalizer.AnalyteHead"/>): общий хвост давал
/// автопривязку к чужой статье — на одном бланке альбумин становился «Белком общим», мочевина и креатинин
/// «Билирубином прямым», глюкоза «Калием», а раз это Hit, правильная статья не заводилась никогда.
/// </summary>
public class LabAnalyteKbLookupService(AppDbContext db)
{
    private const double AutoLinkConfidence = 0.55;
    private const double CandidateConfidence = 0.35;
    private const double TrigramFloor = 0.3;

    public async Task<KbLookupResult> LookupAsync(
        string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedName)) return KbLookupResult.Miss;

        var specific = await LookupBySpecimenAsync(normalizedName, specimenKbId, ct);
        if (specific.Kind != KbLookupKind.Miss) return specific;

        // Фолбэк на обобщённую запись — только когда под конкретный источник ничего не нашлось,
        // и только если сам источник не сентинел "не определено" (иначе это был бы тот же самый
        // запрос дважды).
        if (specimenKbId == SpecimenContextIds.Unresolved) return KbLookupResult.Miss;
        return await LookupBySpecimenAsync(normalizedName, SpecimenContextIds.Unresolved, ct);
    }

    private async Task<KbLookupResult> LookupBySpecimenAsync(string normalizedName, Guid specimenKbId, CancellationToken ct)
    {
        if (await LookupExactOrAliasAsync(normalizedName, specimenKbId, ct) is { } direct) return direct;

        // LOINC-название («мочевина молярная концентрация в сыворотке …») — точное совпадение по голове находит
        // статью, заведённую под коротким именем ("мочевина").
        var head = LabAnalyteNormalizer.AnalyteHead(normalizedName);
        if (head.Length > 0 && head != normalizedName &&
            await LookupExactOrAliasAsync(head, specimenKbId, ct) is { } byHead)
            return byHead;

        var fuzzyCandidates = await db.Database.SqlQuery<LabAnalyteFuzzyRow>($"""
            SELECT "Id", "DisplayName", "NormalizedName",
                   GREATEST(
                       similarity("NormalizedName", {normalizedName}),
                       similarity("DisplayName", {normalizedName})
                   ) AS "Score"
            FROM kb.global_lab_analytes_kb
            WHERE "SpecimenKbId" = {specimenKbId}
              AND (search_vector @@ plainto_tsquery('russian', {normalizedName})
               OR similarity("NormalizedName", {normalizedName}) > {TrigramFloor}
               OR similarity("DisplayName", {normalizedName}) > {TrigramFloor})
            ORDER BY "Score" DESC
            LIMIT {FuzzyCandidateLimit}
            """).ToListAsync(ct);

        // Длинный общий хвост LOINC-названий сам по себе даёт схожесть выше порога автопривязки между РАЗНЫМИ
        // аналитами ("альбумин массовая концентрация в …" ~ "белок общий массовая концентрация в …") — если
        // дескриптор есть хотя бы с одной стороны, кандидат годится только при совпадении голов.
        var fuzzy = fuzzyCandidates.FirstOrDefault(c => HeadsCompatible(normalizedName, head, c.NormalizedName));

        if (fuzzy is null) return KbLookupResult.Miss;
        if (fuzzy.Score >= AutoLinkConfidence) return KbLookupResult.Hit(fuzzy.Id, fuzzy.DisplayName, fuzzy.Score);
        if (fuzzy.Score >= CandidateConfidence) return KbLookupResult.Candidate(fuzzy.Id, fuzzy.DisplayName, fuzzy.Score);
        return KbLookupResult.Miss;
    }

    private async Task<KbLookupResult?> LookupExactOrAliasAsync(string name, Guid specimenKbId, CancellationToken ct)
    {
        var exact = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_lab_analytes_kb
            WHERE "NormalizedName" = {name} AND "SpecimenKbId" = {specimenKbId}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (exact is not null) return KbLookupResult.Hit(exact.Id, exact.DisplayName, exact.Score);

        var alias = await db.Database.SqlQuery<KbLookupRow>($"""
            SELECT "Id", "DisplayName", 1.0::double precision AS "Score"
            FROM kb.global_lab_analytes_kb
            WHERE {name} = ANY("Aliases") AND "SpecimenKbId" = {specimenKbId}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return alias is not null ? KbLookupResult.Hit(alias.Id, alias.DisplayName, alias.Score) : null;
    }

    private static bool HeadsCompatible(string queryName, string queryHead, string candidateName)
    {
        if (!LabAnalyteNormalizer.HasLoincDescriptor(queryName) && !LabAnalyteNormalizer.HasLoincDescriptor(candidateName))
            return true;
        return string.Equals(queryHead, LabAnalyteNormalizer.AnalyteHead(candidateName), StringComparison.Ordinal);
    }

    /// <summary>Сколько лучших нечётких кандидатов просматривается — первый по схожести может отпасть по
    /// голове (<see cref="HeadsCompatible"/>), и тогда шанс получает следующий.</summary>
    private const int FuzzyCandidateLimit = 5;
}

/// <summary>Проекция нечёткого шага <see cref="LabAnalyteKbLookupService"/> — KbLookupRow + NormalizedName для сверки голов.</summary>
internal sealed class LabAnalyteFuzzyRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public double Score { get; set; }
}
