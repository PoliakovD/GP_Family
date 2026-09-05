namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>Резолвит активную модель LM Studio из админки — тот же приём, что IPromptProvider:
/// активное значение из БД (LmStudioModelConfig), фолбэк на LmStudioOptions.Model (appsettings/
/// env), если админ ничего не выбрал. См. class doc LmStudioModelProvider.</summary>
public interface ILmStudioModelProvider
{
    Task<string> GetActiveModelAsync(string fallback, CancellationToken ct = default);

    /// <summary>Вызывать сразу после смены модели из админки — иначе следующий вызов LM Studio
    /// мог бы до 5 минут использовать уже неактуальную закэшированную модель.</summary>
    void Invalidate();
}
