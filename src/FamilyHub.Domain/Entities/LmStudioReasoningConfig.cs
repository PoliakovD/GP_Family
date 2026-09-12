using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Единственная строка — какой уровень "размышлений" (см. LmStudioReasoning) активен для всех
/// вызовов chat/completions (см. ILmStudioModelProvider.GetActiveReasoningAsync в
/// Infrastructure.LmStudio). Отсутствие строки — фолбэк на захардкоженный LmStudioOptions.Reasoning
/// (appsettings/env) — точное зеркало LmStudioModelConfig, тот же приём. Управляется из админки
/// (GET/PUT /api/admin/lmstudio/reasoning) — позволяет сравнивать скорость/глубину рассуждений на
/// лету, без передеплоя.
/// </summary>
public class LmStudioReasoningConfig
{
    public Guid Id { get; set; }

    public LmStudioReasoning Reasoning { get; set; }

    public DateTime UpdatedAt { get; set; }
}
