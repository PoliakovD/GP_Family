using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Authorization;

/// <summary>
/// Императивные проверки доступа по familyId — единственный реальный путь авторизации
/// семейных ресурсов в проекте, что для эндпоинтов, где ресурс ещё не загружен (например,
/// создание нового Medication в семье), что для уже загруженного из БД: каждый сервис вызывает
/// HasRoleAsync сам, явно, в точке мутации/чтения (см. любой *Service.cs в Api/Features и
/// Modules.Medical/Modules.Birthdays). Декларативной resource-based авторизации через
/// ASP.NET Core IAuthorizationHandler в проекте нет.
/// </summary>
public interface IFamilyAccessService
{
    /// <summary>true, если userId — активный (Status == Active) член семьи с ролью >= minRole.</summary>
    Task<bool> HasRoleAsync(Guid userId, Guid familyId, FamilyRole minRole, CancellationToken ct = default);

    /// <summary>Список Id семей, где userId состоит активным членом. Базовый фильтр для списков (инвариант 1).</summary>
    Task<List<Guid>> GetActiveFamilyIdsAsync(Guid userId, CancellationToken ct = default);
}
