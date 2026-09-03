namespace FamilyHub.Domain.Entities;

/// <summary>
/// Одна версия текста промпта (управление enrich-пайплайном из админки, §2 плана) — правка из
/// админки создаёт НОВУЮ версию и активирует её, ничего не удаляя: откат — активация старой
/// версии тем же способом. Не более одной активной версии на <see cref="PipelinePrompt"/>
/// одновременно (частичный уникальный индекс, см. PipelinePromptVersionConfiguration).
/// </summary>
public class PipelinePromptVersion
{
    public Guid Id { get; set; }

    public Guid PromptId { get; set; }

    public PipelinePrompt Prompt { get; set; } = null!;

    /// <summary>1, 2, 3… в пределах одного PromptId — не глобальный счётчик.</summary>
    public int Version { get; set; }

    public string Body { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    /// <summary>Что изменилось в этой версии — заполняется тем, кто правит, произвольный текст
    /// для истории, не участвует в логике.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}
