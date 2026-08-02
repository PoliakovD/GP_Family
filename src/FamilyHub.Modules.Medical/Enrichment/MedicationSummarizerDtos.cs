namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>Версия схемы <see cref="MedicationSummary"/> — записывается в GlobalMedicationKb.PayloadVersion.
/// v2: добавлено поле Usage (способ применения/дозы как в инструкции) — старые строки (v1) читаются
/// как есть, Usage у них просто null (см. KbCatalogService.ParsePayload).
/// v3: добавлено поле SimplePurpose (назначение бытовым языком, не медицинским) — старые строки (v1/v2)
/// читаются как есть, SimplePurpose у них просто null.</summary>
public static class MedicationSummarySchema
{
    public const int CurrentVersion = 3;
}

/// <summary>
/// Обезличенное знание о препарате, извлечённое суммаризатором из веб-сниппетов доверенных
/// источников. Включает общие данные по применению и рекомендации ровно в том объёме, в каком они
/// есть в цитируемой официальной инструкции (Usage/SpecialNotes) — это разделы самой инструкции к
/// препарату, а не персональная медицинская консультация: пайплайн не получает возраст/вес/диагноз
/// пользователя, поэтому персонализировать дозу физически нечем. Грань, которую по-прежнему нельзя
/// пересекать, — не сама широта содержания, а обоснованность: антигаллюцинационный гейт
/// (usedSourceIndexes) и обязательная привязка к трём доверенным источникам (см. ADR-0005 п.8)
/// остаются. Дисклеймер «не медицинская консультация» — в UI (KbCardComponent), не в ограничении
/// полей здесь.
/// </summary>
public record MedicationSummary(
    string? InternationalName,
    IReadOnlyList<string> TradeNames,
    string? Form,
    string? Purpose,
    // Назначение обычным бытовым языком, а не медицинскими терминами (напр. "сбивает температуру"
    // вместо "жаропонижающее") — для пользователей без медицинского образования, см. Purpose.
    string? SimplePurpose,
    string? Usage,
    string? Storage,
    string? Driving,
    string? SpecialNotes,
    IReadOnlyList<int> UsedSourceIndexes,
    // Заполнено, только если цитируемые источники явно указывают, что переданное название
    // (обычно — результат OCR по фото упаковки) искажено, и модель может назвать настоящее
    // название препарата. См. MedicationEnrichmentProcessor — при низкой схожести с исходным
    // именем (похоже на другой препарат, не на опечатку) коррекция отбрасывается.
    string? CorrectedName = null);

/// <summary>Итог суммаризации: либо знание, прошедшее антигаллюцинационный гейт, либо причина отказа записи в справочник.</summary>
public record SummarizeResult(bool Success, MedicationSummary? Summary, string? Error)
{
    public static SummarizeResult Failure(string error) => new(false, null, error);
    public static SummarizeResult Ok(MedicationSummary summary) => new(true, summary, null);
}
