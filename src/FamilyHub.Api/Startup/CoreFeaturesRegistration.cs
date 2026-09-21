using FamilyHub.Api.Features.Dependents;
using FamilyHub.Api.Features.Families;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Api.Features.Members;
using FamilyHub.Infrastructure.Authorization;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — семьи/приглашения/участники/подопечные +
/// авторизация по семейным ролям, без изменения поведения/порядка.
/// </summary>
public static class CoreFeaturesRegistration
{
    public static WebApplicationBuilder AddFamilyHubCoreFeatures(this WebApplicationBuilder builder)
    {
        // --- Core-фичи: семьи, приглашения, участники, подопечные ---
        builder.Services.AddScoped<FamilyService>();
        builder.Services.AddScoped<InviteService>();
        builder.Services.AddScoped<MembershipService>();
        builder.Services.AddScoped<FamilyDependentService>();

        // --- Авторизация по ролям в семье --- Единственный реальный путь — ручные вызовы
        // IFamilyAccessService.HasRoleAsync внутри сервисов (см. каждый *Service.cs в Api/Features и
        // Modules.Medical/Modules.Birthdays). Раньше здесь же регистрировался FamilyRoleHandler —
        // resource-based IAuthorizationHandler<FamilyRoleRequirement, IFamilyOwned> — но ни один
        // эндпоинт/политика нигде не ссылались на FamilyRoleRequirement (аудит, находка High #5):
        // зарегистрированный, но нигде не вызываемый handler создавал ложное впечатление, что часть
        // защиты идёт декларативно через ASP.NET Core policy-инфраструктуру, и мог заставить будущего
        // автора нового эндпоинта пропустить ручную проверку HasRoleAsync, понадеявшись на несуществующую
        // декларативную защиту. Удалён вместе с FamilyRoleRequirement — реальная защита не изменилась,
        // она и раньше была только в HasRoleAsync.
        builder.Services.AddScoped<IFamilyAccessService, FamilyAccessService>();

        return builder;
    }
}
