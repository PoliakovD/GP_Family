namespace FamilyHub.Domain.Entities;

/// <summary>
/// Слот промпта (управление enrich-пайплайном из админки, §2 плана) — стабильный ключ
/// ("analysis.extract", "lab-analyte.summarize" и т.п.), под которым живут версии текста
/// (<see cref="PipelinePromptVersion"/>). Само поле-константа в коде (LmStudioMedicalDocumentExtractor.
/// AnalysisSystemPrompt и т.п.) остаётся источником ФОЛБЭКА — если для ключа нет активной версии в
/// БД, конвейер использует захардкоженный текст (см. PromptProvider), поэтому пустая БД не ломает
/// прод. Инфраструктурное состояние, не медданные — живёт в схеме public.
/// </summary>
public class PipelinePrompt
{
    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
