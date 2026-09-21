using Amazon.SimpleEmailV2;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — PWA-auth сервисы + email-отправка (задача
/// 2.5, расширено 2026-08-19), без изменения поведения/порядка.
/// </summary>
public static class EmailRegistration
{
    public static WebApplicationBuilder AddFamilyHubEmail(this WebApplicationBuilder builder)
    {
        // Email:PostboxApi (HTTPS, порт 443) и/или Email:Providers (SMTP, failover, задача 2.5) —
        // оба канала опциональны и независимы; если задан хотя бы один — CompositeEmailSender пробует
        // их по порядку (Postbox API первым: SMTP-порты 587/465 оказались заблокированы у части
        // провайдеров связи, см. отладку 2026-08-19). Ни один не задан — LoggingEmailSender (dev).
        builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
        builder.Services.AddScoped<EmailOtpService>();
        builder.Services.AddScoped<PwaAuthService>();
        builder.Services.AddScoped<TelegramBindingService>();
        // HTML-вёрстка писем (see docs plan): рендерер не зависит от того, какой IEmailSender выбран
        // ниже — регистрируем его безусловно, чтобы dev/тесты тоже видели настоящий рендер (опечатка
        // в плейсхолдере шаблона должна ронять сборку/тесты, а не только молчать в проде).
        builder.Services.AddSingleton<EmailTemplateRenderer>();
        var emailProvidersConfigured = builder.Configuration.GetSection($"{EmailOptions.SectionName}:Providers").GetChildren().Any();
        var postboxApiSection = builder.Configuration.GetSection($"{EmailOptions.SectionName}:PostboxApi");
        var postboxApiConfigured = postboxApiSection.GetChildren().Any();
        if (postboxApiConfigured)
        {
            var postboxApiOptions = postboxApiSection.Get<YandexPostboxApiOptions>() ?? new YandexPostboxApiOptions();
            if (string.IsNullOrWhiteSpace(postboxApiOptions.AccessKeyId) || string.IsNullOrWhiteSpace(postboxApiOptions.SecretAccessKey)
                || string.IsNullOrWhiteSpace(postboxApiOptions.From))
            {
                throw new InvalidOperationException(
                    "Email:PostboxApi задан, но AccessKeyId/SecretAccessKey/From (env Email__PostboxApi__*) пусты — " +
                    "это отдельный статический access-key Yandex Cloud, не логин/пароль SMTP.");
            }

            builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => new AmazonSimpleEmailServiceV2Client(
                postboxApiOptions.AccessKeyId, postboxApiOptions.SecretAccessKey,
                new AmazonSimpleEmailServiceV2Config { ServiceURL = postboxApiOptions.ServiceUrl, AuthenticationRegion = postboxApiOptions.Region }));
            builder.Services.AddSingleton<YandexPostboxApiEmailSender>();
        }
        if (emailProvidersConfigured)
        {
            builder.Services.AddSingleton<ISmtpTransport, MailKitSmtpTransport>();
            builder.Services.AddSingleton<MailKitSmtpEmailSender>();
        }
        if (postboxApiConfigured || emailProvidersConfigured)
        {
            // Хотя бы один канал задан ⇒ письма уходят наружу ⇒ ссылка в кнопке «Открыть FamilyHub»
            // обязана быть настоящей. Проверка схемы заодно закрывает подстановку javascript:-URL в
            // href шаблона.
            var publicSiteUrl = builder.Configuration[$"{EmailOptions.SectionName}:PublicSiteUrl"];
            if (!Uri.TryCreate(publicSiteUrl, UriKind.Absolute, out var siteUri)
                || (siteUri.Scheme != Uri.UriSchemeHttps && siteUri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException(
                    "Email:PublicSiteUrl должен быть абсолютным http(s)-URL — он подставляется в кнопку «Открыть FamilyHub» в письмах.");
            }

            // Порядок важен: Postbox API — первым (подтверждённо работает через 443), SMTP — резервным.
            builder.Services.AddSingleton<IEmailSender>(sp =>
            {
                var channels = new List<IEmailSender>();
                if (postboxApiConfigured) channels.Add(sp.GetRequiredService<YandexPostboxApiEmailSender>());
                if (emailProvidersConfigured) channels.Add(sp.GetRequiredService<MailKitSmtpEmailSender>());
                return new CompositeEmailSender(channels, sp.GetRequiredService<ILogger<CompositeEmailSender>>());
            });
        }
        else
        {
            builder.Services.AddScoped<IEmailSender, LoggingEmailSender>();
        }

        return builder;
    }
}
