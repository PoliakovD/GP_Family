namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>Имена httpOnly-cookie PWA-сессии.</summary>
public static class PwaCookieNames
{
    /// <summary>Короткоживущий JWT access-токен, читается JwtBearer-обработчиком на каждый запрос.</summary>
    public const string AccessToken = "familyhub.at";

    /// <summary>Долгоживущий refresh-токен; отправляется браузером только на Path=/api/auth.</summary>
    public const string RefreshToken = "familyhub.rt";
}
