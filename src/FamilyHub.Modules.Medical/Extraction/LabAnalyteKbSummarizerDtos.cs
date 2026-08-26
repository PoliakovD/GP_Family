using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Версия схемы <see cref="LabAnalyteSummary"/> — записывается в GlobalLabAnalyteKb.PayloadVersion
/// (зеркало MedicationSummarySchema).
/// v2: добавлены Sex на LabAnalyteReferenceRange и LabAnalyteSummary.CalculationInstructions —
/// старые строки (v1) читаются как есть, оба поля просто null/отсутствуют.</summary>
public static class LabAnalyteSummarySchema
{
    public const int CurrentVersion = 2;
}

/// <summary>Один референсный диапазон из PayloadJson.refRanges. Sex=null — общий диапазон (годится
/// любому полу); задан — диапазон специфичен для этого пола (домен теперь хранит пол — identity
/// rework, User.Gender/FamilyDependent.Gender — см. IndicatorFlagCalculator.MatchesPatient).
/// AgeFrom/AgeTo оба null — диапазон общий, без возрастных ограничений.</summary>
public record LabAnalyteReferenceRange(int? AgeFrom, int? AgeTo, Gender? Sex, double? Low, double? High, string? Unit);

/// <summary>
/// Обезличенное знание о лабораторном показателе, извлечённое суммаризатором из веб-сниппетов
/// доверенных источников (лаборатории/лабораторные справочники — EnrichmentOptions.AnalyteTrustedDomains).
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
    string? CalculationInstructions = null);

/// <summary>Итог суммаризации: либо знание, прошедшее антигаллюцинационный гейт, либо причина отказа записи в справочник.</summary>
public record LabAnalyteSummarizeResult(bool Success, LabAnalyteSummary? Summary, string? Error)
{
    public static LabAnalyteSummarizeResult Failure(string error) => new(false, null, error);
    public static LabAnalyteSummarizeResult Ok(LabAnalyteSummary summary) => new(true, summary, null);
}
