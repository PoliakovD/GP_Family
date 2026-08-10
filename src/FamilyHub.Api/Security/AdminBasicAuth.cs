using System.Security.Cryptography;
using System.Text;
using FamilyHub.Api.Configuration;

namespace FamilyHub.Api.Security;

/// <summary>
/// Общая проверка Basic-авторизации для служебных UI (Hangfire-дашборд, Swagger), которые на
/// VPS доступны при DevTools:AdminUiEnabled=true поверх WireGuard-периметра. Вынесена отдельно
/// от <see cref="HangfireBasicAuthFilter"/>, чтобы Swagger-гейт в Program.cs не дублировал ту же
/// постоянно-временную проверку логина/пароля.
/// </summary>
public static class AdminBasicAuth
{
    public static bool IsAuthorized(HttpContext httpContext, DevToolsOptions devTools)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var header)
            || !header.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        string decoded;
        try
        {
            var encoded = header.ToString()["Basic ".Length..].Trim();
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex < 0)
            return false;

        var user = decoded[..separatorIndex];
        var password = decoded[(separatorIndex + 1)..];

        var userMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(devTools.AdminUser ?? string.Empty));
        var passwordMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(devTools.AdminPassword ?? string.Empty));

        return userMatches && passwordMatches;
    }

    public static void Challenge(HttpContext httpContext)
    {
        httpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"FamilyHub admin\"";
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }
}
