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

    /// <summary>Host-заголовки (с портом, если он не стандартный), на которых обслуживаются /api/admin/*
    /// и SPA-маршруты /admin — например <c>admin.example.ru:4059</c>. Пусто — без ограничения (дев/тесты;
    /// на старте пишется предупреждение). Аудит security-audit-2026-10, M3: без этого панель отделена от
    /// публичного домена только путевым фильтром Caddy. Env: <c>Admin__AllowedHosts__0</c>.</summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Сколько неудачных входов подряд (в окне <see cref="LockoutDuration"/>) блокирует форму входа.
    /// Счётчик общий, не по IP: логин у панели один, а сама она доступна только из-за WireGuard.</summary>
    public int MaxFailedLogins { get; set; } = 10;

    /// <summary>Окно подсчёта неудач и длительность блокировки входа.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Секрет второго фактора (TOTP, RFC 6238) в base32 — его же один раз добавляют в
    /// приложение-аутентификатор. Env: <c>Admin__TotpSecret</c>. Аудит security-audit-2026-10, бэклог M3.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Требовать код TOTP при входе (по умолчанию да; при <see cref="Enabled"/> без валидного
    /// <see cref="TotpSecret"/> хост не стартует). Выключать только осознанно — например, в изолированном
    /// e2e-стеке.</summary>
    public bool RequireTotp { get; set; } = true;
}
