using MailKit.Security;
using MimeKit;

namespace FamilyHub.Infrastructure.Email;

public class MailKitSmtpTransport : ISmtpTransport
{
    public async Task SendAsync(SmtpProviderOptions provider, string to, string subject, string textBody, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(provider.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = textBody };

        using var client = new MailKit.Net.Smtp.SmtpClient();
        await client.ConnectAsync(
            provider.Host, provider.Port,
            provider.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(provider.User, provider.Password, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
