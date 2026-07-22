namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Абстракция отправки email (mirrors INotificationSender). Реализации:
/// LoggingEmailSender (dev-заглушка) и MailKitSmtpEmailSender (российские SMTP-провайдеры
/// с failover, задача 2.5). Выбор — в Program.cs по наличию Email:Providers.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);
}
