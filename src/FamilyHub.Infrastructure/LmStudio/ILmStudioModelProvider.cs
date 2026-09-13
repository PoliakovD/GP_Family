using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>Резолвит активную модель/уровень размышлений LM Studio из админки — тот же приём, что
/// IPromptProvider: активное значение из БД (LmStudioModelConfig/LmStudioReasoningConfig),
/// фолбэк на LmStudioOptions (appsettings/env), если админ ничего не выбрал. См. class doc
/// LmStudioModelProvider.</summary>
public interface ILmStudioModelProvider
{
    Task<string> GetActiveModelAsync(string fallback, CancellationToken ct = default);

    /// <summary>Вызывать сразу после смены модели из админки — иначе следующий вызов LM Studio
    /// мог бы до 5 минут использовать уже неактуальную закэшированную модель.</summary>
    void Invalidate();

    Task<LmStudioReasoning> GetActiveReasoningAsync(LmStudioReasoning fallback, CancellationToken ct = default);

    /// <summary>Вызывать сразу после смены уровня размышлений из админки — та же причина, что у
    /// Invalidate() выше.</summary>
    void InvalidateReasoning();
}
