using FamilyHub.Infrastructure.Consents;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Birthdays.Birthdays;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.Modules.Birthdays;

/// <summary>
/// Точка входа модуля «Дни рождения» (этап 4 п.11 брифа — модуль зависит только от
/// Domain/Infrastructure, не от других модулей).
/// </summary>
public static class BirthdayModule
{
    public static IServiceCollection AddBirthdayModule(this IServiceCollection services)
    {
        services.AddScoped<BirthdayService>();
        // Источник для глобального поиска (SearchService, Modules.Medical) — DI-абстракция из
        // Infrastructure, без прямой зависимости между модулями (см. IBirthdaySearchSource).
        services.AddScoped<IBirthdaySearchSource, BirthdaySearchSource>();
        return services;
    }

    public static void MapBirthdayModule(this IEndpointRouteBuilder app)
    {
        // Дни рождения = ПДн (имя + дата) → та же консент-гарантия, что и у Medical (задача 2.3).
        app.MapGroup("").AddEndpointFilter<ConsentRequiredFilter>().MapBirthdayEndpoints();
    }
}
