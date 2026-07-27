namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Тонкий шов над MailKit SmtpClient — единственная причина существования: юнит-тесты
/// failover-логики MailKitSmtpEmailSender без реального SMTP-соединения.
/// </summary>
public interface ISmtpTransport
{
    Task SendAsync(SmtpProviderOptions provider, string to, string subject, EmailBody body, CancellationToken ct);
}
