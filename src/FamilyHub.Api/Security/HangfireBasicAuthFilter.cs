using FamilyHub.Api.Configuration;
using Hangfire.Dashboard;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Security;

/// <summary>
/// Второй рубеж защиты Hangfire-дашборда — поверх периметра (Caddy пускает /hangfire только на
/// WireGuard-адресе). Раньше <see cref="DashboardOptions.Authorization"/> был пуст (см. историю
/// Program.cs) под прикрытием IsDevelopment() — теперь дашборд доступен и на VPS (DevTools:AdminUiEnabled),
/// поэтому нужна собственная проверка, а не голое AllowAnonymous. Сама проверка — в
/// <see cref="AdminBasicAuth"/>, общей со Swagger-гейтом в Program.cs.
/// </summary>
public class HangfireBasicAuthFilter(IOptions<DevToolsOptions> options) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        if (AdminBasicAuth.IsAuthorized(httpContext, options.Value))
            return true;

        AdminBasicAuth.Challenge(httpContext);
        return false;
    }
}
