namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Версия схемы <see cref="LabAnalyteSummary"/> — записывается в GlobalLabAnalyteKb.PayloadVersion
/// (зеркало MedicationSummarySchema).</summary>
public static class LabAnalyteSummarySchema
{
    public const int CurrentVersion = 1;
}

/// <summary>Один референсный диапазон из PayloadJson.refRanges — без разбиения по полу (домен его
/// нигде не хранит, см. IndicatorFlagCalculator), только по возрасту. AgeFrom/AgeTo оба null —
/// диапазон общий, без возрастных ограничений.</summary>
public record LabAnalyteReferenceRange(int? AgeFrom, int? AgeTo, double? Low, double? High, string? Unit);

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
    IReadOnlyList<int> UsedSourceIndexes);

/// <summary>Итог суммаризации: либо знание, прошедшее антигаллюцинационный гейт, либо причина отказа записи в справочник.</summary>
public record LabAnalyteSummarizeResult(bool Success, LabAnalyteSummary? Summary, string? Error)
{
    public static LabAnalyteSummarizeResult Failure(string error) => new(false, null, error);
    public static LabAnalyteSummarizeResult Ok(LabAnalyteSummary summary) => new(true, summary, null);
}
