namespace FamilyHub.Api.Configuration;

/// <summary>
/// Инструменты, которые раньше жёстко гейтились на <c>IsDevelopment()</c> (Hangfire-дашборд,
/// Swagger, <c>DevAuthenticationHandler</c>, эндпоинты <c>/dev/*</c>). Дев-контур на VPS работает
/// под <c>ASPNETCORE_ENVIRONMENT=Production</c> (иначе включается <c>DeveloperExceptionPage</c>,
/// который отдаёт стектрейс клиенту), поэтому эти инструменты вынесены на отдельные флаги
/// (секция "DevTools"), управляемые через .env — независимо от среды хостинга.
/// </summary>
public class DevToolsOptions
{
    public const string SectionName = "DevTools";

    /// <summary>
    /// Регистрирует <c>DevAuthenticationHandler</c> (схема "Dev") и ветку выбора схемы по
    /// заголовку X-Dev-TelegramId — вход под любым TelegramId без пароля. Только для локальной
    /// разработки; на VPS всегда false. Также управляет guard'ом на утёкший design-time
    /// Encryption:MasterKey (см. Program.cs) — вне зависимости от среды хостинга.
    /// </summary>
    public bool DevAuthEnabled { get; set; }

    /// <summary>
    /// Регистрирует служебные эндпоинты /dev/trigger-reminder-scan, /dev/trigger-enrichment/{id},
    /// /dev/email-preview/{name}. Не требуют аутентификации по построению — только для локали.
    /// </summary>
    public bool DevEndpointsEnabled { get; set; }

    /// <summary>
    /// Включает Hangfire-дашборд ("/hangfire") и Swagger UI ("/swagger"). На VPS — true, но
    /// закрыт собственным BasicAuth (<see cref="AdminUser"/>/<see cref="AdminPassword"/>) поверх
    /// периметра WireGuard/Caddy — периметр не единственный рубеж защиты.
    /// </summary>
    public bool AdminUiEnabled { get; set; }

    public string? AdminUser { get; set; }

    public string? AdminPassword { get; set; }
}
