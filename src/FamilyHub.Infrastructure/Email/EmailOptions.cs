namespace FamilyHub.Infrastructure.Email;

/// <summary>Настройки SMTP-провайдера. Провайдеры перечислены в порядке приоритета.</summary>
public class SmtpProviderOptions
{
    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Адрес отправителя (From).</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Суточный лимит писем провайдера; null — без лимита. При исчерпании — failover.</summary>
    public int? DailyLimit { get; set; }
}

/// <summary>
/// Секция "Email" (задача 2.5): список российских SMTP-провайдеров с автопереключением.
/// Пусто — регистрируется LoggingEmailSender (dev).
/// </summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public List<SmtpProviderOptions> Providers { get; set; } = [];
}
