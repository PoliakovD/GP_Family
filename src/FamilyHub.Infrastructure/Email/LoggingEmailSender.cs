using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Dev-заглушка: письмо пишется в лог вместо отправки (в т.ч. код подтверждения — иначе
/// локально войти в PWA невозможно). В прод-конфиге обязаны быть заданы Email:Providers.
/// </summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        logger.LogInformation("EMAIL (заглушка) → {To}: [{Subject}] {Body}", to, subject, textBody);
        return Task.CompletedTask;
    }
}
