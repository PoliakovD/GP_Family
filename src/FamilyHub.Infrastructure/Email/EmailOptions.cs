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

    /// <summary>
    /// Отображаемое имя отправителя. Пусто — письмо уходит с голым адресом. В From его лучше не
    /// встраивать: значение приходит из .env, где кавычки/угловые скобки разные docker compose
    /// обрабатывают по-разному, а SMTP-релей (Yandex Postbox и т.п.) валидирует именно адрес —
    /// имя в отдельном поле никогда не сможет его сломать.
    /// </summary>
    public string FromDisplayName { get; set; } = "FamilyHub";

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

    /// <summary>
    /// Абсолютный URL сайта для кнопки «Открыть FamilyHub» в письмах. Значение по умолчанию,
    /// а не required: пустой Providers — это то, что выбирает LoggingEmailSender (dev/тесты), и
    /// требовать реальный домен там незачем. Где это реально важно (Providers заданы ⇒ письма
    /// уходят наружу) — fail-fast в Program.cs проверяет, что это настоящий http(s)-URL.
    /// </summary>
    public string PublicSiteUrl { get; set; } = "https://gp-family.ru";
}
