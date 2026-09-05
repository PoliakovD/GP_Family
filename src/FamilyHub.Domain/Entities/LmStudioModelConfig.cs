namespace FamilyHub.Domain.Entities;

/// <summary>
/// Единственная строка — какая модель LM Studio активна для всех вызовов chat/completions
/// (см. ILmStudioModelProvider в Infrastructure.LmStudio). Отсутствие строки — фолбэк на
/// захардкоженный LmStudioOptions.Model (appsettings/env), тот же приём, что отсутствие активной
/// PipelinePromptVersion — PromptProvider. Управляется из админки (GET/PUT
/// /api/admin/lmstudio/model), локальная модель меняется чаще, чем требует передеплой.
/// </summary>
public class LmStudioModelConfig
{
    public Guid Id { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
