namespace FamilyHub.Api.Configuration;

/// <summary>
/// Логин/пароль и время жизни сессии админ-панели (ADR-0009) — раздел статистики и ротации
/// ключей на отдельном домене admin.{PUBLIC_DOMAIN}:4059, за периметром WireGuard/Caddy.
/// Отдельная секция от DevTools:AdminUser/AdminPassword намеренно: та пара защищает Hangfire/
/// Swagger и по плану 00-INDEX.md должна со временем исчезнуть вместе с dev-заглушками, тогда
/// как эта — постоянная продуктовая поверхность, и её пароль должен ротироваться независимо.
/// </summary>
public class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Включает /api/admin/* и форму входа. Fail-fast при true без User/Password (Program.cs).</summary>
    public bool Enabled { get; set; }

    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>Как долго действует cookie сессии после входа (абсолютная, без sliding-продления).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);
}
