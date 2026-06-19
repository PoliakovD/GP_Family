using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Authorization;

/// <summary>
/// Императивные проверки доступа по familyId, взятому из роута — для эндпоинтов, где
/// ресурс ещё не загружен (например, создание нового Medication в семье). Когда ресурс
/// уже загружен из БД, предпочтительнее resource-based авторизация через FamilyRoleHandler.
/// </summary>
public interface IFamilyAccessService
{
    /// <summary>true, если userId — активный (Status == Active) член семьи с ролью >= minRole.</summary>
    Task<bool> HasRoleAsync(Guid userId, Guid familyId, FamilyRole minRole, CancellationToken ct = default);

    /// <summary>Список Id семей, где userId состоит активным членом. Базовый фильтр для списков (инвариант 1).</summary>
    Task<List<Guid>> GetActiveFamilyIdsAsync(Guid userId, CancellationToken ct = default);
}
