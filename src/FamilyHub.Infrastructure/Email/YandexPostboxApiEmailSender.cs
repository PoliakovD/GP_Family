using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Отправка через SESv2-совместимый HTTPS API Yandex Cloud Postbox
/// (https://postbox.cloud.yandex.net/v2/email/outbound-emails, порт 443) — добавлено 2026-08-19
/// как обход блокировки исходящих SMTP-портов 587/465 у провайдера связи (подтверждено и на
/// проде, и локально у разработчика; 443 у тех же адресатов доступен). IAmazonSimpleEmailServiceV2
/// внедряется через DI (не создаётся здесь) — единственная причина: юнит-тесты без реального
/// HTTPS-вызова (см. YandexPostboxApiEmailSenderTests), тот же приём, что ISmtpTransport у
/// MailKitSmtpEmailSender.
/// </summary>
public class YandexPostboxApiEmailSender(
    IAmazonSimpleEmailServiceV2 client, IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, EmailBody body, CancellationToken ct = default)
    {
        var postbox = options.Value.PostboxApi
            ?? throw new InvalidOperationException(
                "YandexPostboxApiEmailSender зарегистрирован, но Email:PostboxApi пуст — ошибка регистрации в Program.cs.");

        // Имя отдельным полем, не встроено в FromEmailAddress: тот же принцип, что у
        // SmtpProviderOptions.FromDisplayName — значение из .env не должно ломать адрес,
        // если в нём окажутся неожиданные кавычки/угловые скобки.
        var from = string.IsNullOrWhiteSpace(postbox.FromDisplayName)
            ? postbox.From
            : $"{postbox.FromDisplayName} <{postbox.From}>";

        var request = new SendEmailRequest
        {
            FromEmailAddress = from,
            Destination = new Destination { ToAddresses = [to] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Text = new Content { Data = body.Text, Charset = "UTF-8" },
                        Html = body.Html is null ? null : new Content { Data = body.Html, Charset = "UTF-8" },
                    },
                },
            },
        };

        await client.SendEmailAsync(request, ct);
    }
}
