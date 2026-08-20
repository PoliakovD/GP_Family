namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Тело письма: текстовая часть обязательна (accessibility-фолбэк, антиспам-сигнал и то, что
/// печатает LoggingEmailSender в dev), HTML — опциональна. Html == null ⇒ письмо уходит как
/// одиночный text/plain, байт-в-байт как до вёрстки (см. MailKitSmtpTransport.BuildMessage).
/// </summary>
public sealed record EmailBody(string Text, string? Html = null);

/// <summary>
/// Абстракция отправки email (mirrors INotificationSender). Реализации: LoggingEmailSender
/// (dev-заглушка), YandexPostboxApiEmailSender (HTTPS API, основной канал, добавлено
/// 2026-08-19) и MailKitSmtpEmailSender (SMTP-провайдеры с failover, задача 2.5, резервный
/// канал) — оба реальных канала пробует CompositeEmailSender по порядку. Выбор — в Program.cs
/// по наличию Email:PostboxApi/Email:Providers.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, EmailBody body, CancellationToken ct = default);
}
