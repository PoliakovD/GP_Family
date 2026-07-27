using MailKit.Security;
using MimeKit;

namespace FamilyHub.Infrastructure.Email;

public class MailKitSmtpTransport : ISmtpTransport
{
    public async Task SendAsync(SmtpProviderOptions provider, string to, string subject, EmailBody body, CancellationToken ct)
    {
        var message = BuildMessage(provider, to, subject, body);

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(
            provider.Host, provider.Port,
            provider.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(provider.User, provider.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    /// <summary>
    /// Сборка MimeMessage вынесена отдельно и public — единственный способ юнит-тестом проверить
    /// multipart/alternative без реального SMTP-соединения.
    /// </summary>
    public static MimeMessage BuildMessage(SmtpProviderOptions provider, string to, string subject, EmailBody body)
    {
        var message = new MimeMessage();

        // MailboxAddress.Parse принимает и "noreply@x.ru", и "FamilyHub <noreply@x.ru>" — если в
        // самом From имя уже указано, не перетираем его FromDisplayName.
        var from = MailboxAddress.Parse(provider.From);
        if (string.IsNullOrEmpty(from.Name) && !string.IsNullOrWhiteSpace(provider.FromDisplayName))
            from = new MailboxAddress(provider.FromDisplayName, from.Address);
        message.From.Add(from);

        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        // Транзакционное письмо: подавляем автоответы («в отпуске») и отметку в отчётах — со
        // свёрстанным HTML это более вероятный кейс, чем было с голым текстом.
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Headers.Add("X-Auto-Response-Suppress", "All");

        var builder = new BodyBuilder { TextBody = body.Text };
        if (!string.IsNullOrWhiteSpace(body.Html))
            builder.HtmlBody = body.Html;

        // TextBody+HtmlBody ⇒ multipart/alternative. Только TextBody ⇒ одиночный text/plain,
        // байт-в-байт как до вёрстки — dev/тестовые конфиги (Html == null) ничего не замечают.
        message.Body = builder.ToMessageBody();
        return message;
    }
}
