namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>Один зарегистрированный слот промпта — ключ + человекочитаемое описание, для сида
/// миграции и для листинга в админке. Не все промпты привязаны к шагу пайплайна (см.
/// PipelineCatalog) — например, "analysis.specimen-validate" вызывается синхронно при ручном
/// вводе источника пользователем (GlobalSpecimenKbService), не является шагом фонового
/// конвейера — но всё равно должен быть редактируем из админки как любой другой промпт.</summary>
public record PromptDeclaration(string Key, string Description);

public static class PromptCatalog
{
    public static readonly IReadOnlyList<PromptDeclaration> Prompts =
    [
        new("analysis.extract", "Структурирование показателей анализа из текста/фото бланка"),
        new("analysis.specimen-resolve", "Резолвинг источника показателя по документу (биоматериал/исследование)"),
        new("visit.extract", "Структурирование заключения врача из текста/фото документа"),
        new("analysis.ocr-correct", "Коррекция OCR-артефактов в названиях показателей/медикаментов"),
        new("analysis.specimen-validate", "Валидация названия источника при ручном вводе пользователем"),
        new("analysis.patient-reference", "Расчёт персонального референса по методике из справочника"),
        new("analysis.record-summary", "Суммаризация показателей записи для пользователя"),
        new("lab-analyte.summarize", "Суммаризация веб-сниппетов в статью справочника показателей"),
        new("medication.summarize", "Суммаризация веб-сниппетов в карточку препарата"),
        new("medication.ocr", "Распознавание медикамента по фото упаковки"),
    ];
}
