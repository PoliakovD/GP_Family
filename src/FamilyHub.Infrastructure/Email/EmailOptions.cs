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
/// Настройки HTTPS-API Yandex Cloud Postbox (SESv2-совместимый, см. YandexPostboxApiEmailSender).
/// Добавлено 2026-08-19: на проде и локально у разработчика исходящие SMTP-порты 587/465
/// оказались заблокированы провайдером связи (не Yandex и не наш код) — 443 при этом доступен,
/// у Postbox есть HTTPS-эндпоинт с той же функциональностью. AccessKeyId/SecretAccessKey —
/// ОТДЕЛЬНЫЙ статический access-key Yandex Cloud (Service Accounts → создать статический
/// ключ), не логин/пароль от SMTP выше — тот для API не годится.
/// </summary>
public class YandexPostboxApiOptions
{
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Регион для AWS SigV4-подписи запроса — у Yandex Cloud Postbox всегда "ru-central1".</summary>
    public string Region { get; set; } = "ru-central1";

    public string ServiceUrl { get; set; } = "https://postbox.cloud.yandex.net";

    public string From { get; set; } = string.Empty;

    public string FromDisplayName { get; set; } = "FamilyHub";
}

/// <summary>
/// Секция "Email" (задача 2.5, расширена 2026-08-19): HTTPS API Yandex Postbox — основной канал
/// (PostboxApi), список SMTP-провайдеров с автопереключением — резервный (Providers, задача 2.5,
/// изначальная реализация). Оба канала пусты — регистрируется LoggingEmailSender (dev).
/// </summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>null — HTTPS API не сконфигурирован (см. CompositeEmailSender/Program.cs).</summary>
    public YandexPostboxApiOptions? PostboxApi { get; set; }

    public List<SmtpProviderOptions> Providers { get; set; } = [];

    /// <summary>
    /// Абсолютный URL сайта для кнопки «Открыть FamilyHub» в письмах. Значение по умолчанию,
    /// а не required: пустые PostboxApi/Providers — это то, что выбирает LoggingEmailSender
    /// (dev/тесты), и требовать реальный домен там незачем. Где это реально важно (любой канал
    /// сконфигурирован ⇒ письма уходят наружу) — fail-fast в Program.cs проверяет, что это
    /// настоящий http(s)-URL.
    /// </summary>
    public string PublicSiteUrl { get; set; } = "https://gp-family.ru";
}
