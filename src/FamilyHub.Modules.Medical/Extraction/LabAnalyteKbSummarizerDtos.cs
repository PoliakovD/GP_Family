using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Версия схемы <see cref="LabAnalyteSummary"/> — записывается в GlobalLabAnalyteKb.PayloadVersion
/// (зеркало MedicationSummarySchema).
/// v2: добавлены Sex на LabAnalyteReferenceRange и LabAnalyteSummary.CalculationInstructions —
/// старые строки (v1) читаются как есть, оба поля просто null/отсутствуют.
/// v3 (редизайн v2, PR4-BE): добавлен LabAnalyteSummary.RelatedAnalytes («Что смотрят вместе» в
/// панели справки) — старые строки (v1/v2) читаются как пустой список, бэкофилл не нужен, поле
/// дозаполняется при следующем прогоне обогащения/ручного рефреша.
/// v4 (пересборка enrich-пайплайна анализов): у справочника появился ключ (показатель, биоматериал)
/// вместо одного имени (см. GlobalLabAnalyteKb.Specimen); LabAnalyteReferenceRange получил
/// систематизированные NormKind/Population/PopulationDetail (вместо неявного вывода из наличия
/// чисел) и SourceDomain/SourceRank, заполняемые детерминированным merge'ем по приоритету
/// доверенных доменов (см. ReferenceRangeMerger), а не моделью. Старые строки (v1-v3) читаются как
/// NormKind=FixedRange, Population=General, SourceDomain=null/SourceRank=0 — принудительное
/// переобогащение (LabAnalyteKbReenrichJob) дозаполняет их следующим прогоном.</summary>
public static class LabAnalyteSummarySchema
{
    public const int CurrentVersion = 4;
}

/// <summary>Один референсный диапазон из PayloadJson.refRanges. Sex=null — общий диапазон (годится
/// любому полу); задан — диапазон специфичен для этого пола (домен теперь хранит пол — identity
/// rework, User.Gender/FamilyDependent.Gender — см. IndicatorFlagCalculator.MatchesPatient).
/// AgeFrom/AgeTo оба null — диапазон общий, без возрастных ограничений.
/// SourceIndex — индекс сниппета (см. LabAnalyteKbSummarizer), из которого модель взяла ИМЕННО этот
/// диапазон; заполняется моделью, используется только внутри ReferenceRangeMerger для вычисления
/// SourceDomain/SourceRank и не попадает в итоговый payload (см. LabAnalyteKbPayload.Build).</summary>
public record LabAnalyteReferenceRange(
    int? AgeFrom, int? AgeTo, Gender? Sex, double? Low, double? High, string? Unit,
    LabNormKind NormKind = LabNormKind.FixedRange,
    LabPopulation Population = LabPopulation.General,
    string? PopulationDetail = null,
    int? SourceIndex = null,
    string? SourceDomain = null,
    int SourceRank = 0);

/// <summary>
/// Обезличенное знание о лабораторном показателе, извлечённое суммаризатором из веб-сниппетов
/// доверенных источников (лаборатории/лабораторные справочники — EnrichmentTrustedDomain, Topic=LabAnalyte).
/// Зеркало MedicationSummary (этап 4) на другой предмет.
/// </summary>
public record LabAnalyteSummary(
    string? LoincCode,
    string? DefaultUnit,
    string? PlainExplanation,
    string? WhyMeasured,
    string? HighMeans,
    string? LowMeans,
    IReadOnlyList<LabAnalyteReferenceRange> RefRanges,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<int> UsedSourceIndexes,
    /// <summary>Словесная методика расчёта нормы, когда она не сводится к фиксированным
    /// диапазонам (зависит от веса/роста/срока беременности и т.п.) — например, формула клиренса
    /// креатинина. Используется PatientReferenceCalculator как RefSource.KbCalculated шаг
    /// каскада, когда RefRanges не дал совпадения под конкретного пациента. Null, если
    /// суммаризатор не нашёл такой методики в источниках (большинство показателей — обычный
    /// фиксированный диапазон, этого поля им не требуется).</summary>
    string? CalculationInstructions = null,
    /// <summary>v3 (редизайн v2) — «Что смотрят вместе» в панели справки: 2-5 нормализованных
    /// имён показателей (тот же LabAnalyteNormalizer.Normalize, что и ключ дедупликации самого
    /// справочника), которые лаборатории/врачи обычно интерпретируют вместе с этим. Резолвятся в
    /// Id живым поиском по NormalizedName на чтении (см. ExtractionQueryService.GetArticleAsync/
    /// KbAnalyteCatalogService), не хранятся как ссылки — статья связанного показателя может
    /// появиться в справочнике позже, чем эта (тот же приём, что PrescribedMedicationDto.KbMedicationId).</summary>
    IReadOnlyList<string>? RelatedAnalytes = null);

/// <summary>Итог суммаризации: либо знание, прошедшее антигаллюцинационный гейт, либо причина отказа записи в справочник.</summary>
public record LabAnalyteSummarizeResult(bool Success, LabAnalyteSummary? Summary, string? Error)
{
    public static LabAnalyteSummarizeResult Failure(string error) => new(false, null, error);
    public static LabAnalyteSummarizeResult Ok(LabAnalyteSummary summary) => new(true, summary, null);
}
