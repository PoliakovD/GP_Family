using System.Security.Claims;

namespace FamilyHub.Infrastructure.Authorization;

public static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(FamilyHubClaimTypes.UserId)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static long? GetTelegramId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst(FamilyHubClaimTypes.TelegramId)?.Value;
        return long.TryParse(raw, out var id) ? id : null;
    }
}
