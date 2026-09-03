namespace FamilyHub.Domain.Entities;

/// <summary>
/// Вкл/выкл одного НЕОБЯЗАТЕЛЬНОГО шага пайплайна из админки (управление enrich-пайплайном, §2
/// плана) — состав и порядок шагов объявлены в коде (см. PipelineCatalog, Modules.Medical): здесь
/// хранится только "включён ли конкретный необязательный шаг", не сама последовательность. Строка
/// без записи в этой таблице для существующей пары (PipelineKey, StepKey) означает "включён"
/// (значение по умолчанию, см. PipelineConfigService.IsEnabledAsync) — заводить строки заранее не
/// нужно, только когда админ реально что-то выключает.
/// </summary>
public class PipelineStepConfig
{
    public Guid Id { get; set; }

    public string PipelineKey { get; set; } = string.Empty;

    public string StepKey { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Параметры шага (например, порог confidence у резолвинга источника) — свободный
    /// JSON, конкретный шаг сам знает, какие ключи ему нужны.</summary>
    public string? ParamsJson { get; set; }

    public DateTime UpdatedAt { get; set; }
}
