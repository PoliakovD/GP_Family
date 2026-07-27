using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Dev-заглушка: письмо пишется в лог вместо отправки (в т.ч. код подтверждения — иначе
/// локально войти в PWA невозможно). В прод-конфиге обязаны быть заданы Email:Providers.
/// </summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    // Логируем только текстовую часть: HTML-версия письма (~6 КБ) забила бы консоль/Seq и не
    // нужна для чтения кода/пароля в dev-цикле — для просмотра вёрстки есть отдельный
    // /dev/email-preview/{name} и EmailPreviewWriter (см. EmailTemplateRenderer).
    public Task SendAsync(string to, string subject, EmailBody body, CancellationToken ct = default)
    {
        logger.LogInformation("EMAIL (заглушка) → {To}: [{Subject}] {Body} (html={HtmlLength} байт)",
            to, subject, body.Text, body.Html?.Length ?? 0);
        return Task.CompletedTask;
    }
}
