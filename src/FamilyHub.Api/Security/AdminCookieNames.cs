namespace FamilyHub.Api.Security;

/// <summary>Имя httpOnly-cookie сессии админ-панели (ADR-0009) — см. AdminAuthenticationHandler.</summary>
public static class AdminCookieNames
{
    public const string Session = "familyhub.admin";
}
