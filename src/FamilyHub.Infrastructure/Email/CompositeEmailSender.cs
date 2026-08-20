using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Пробует каналы по порядку, переключаясь на следующий при сбое — тот же приём, что у
/// MailKitSmtpEmailSender.SendAsync (провайдеры внутри одного канала), но уровнем выше: между
/// РАЗНЫМИ транспортами (HTTPS API Yandex Postbox, SMTP). Добавлено 2026-08-19, когда SMTP
/// оказался заблокирован провайдером связи — порядок каналов задаёт Program.cs (HTTPS API
/// первым, поскольку он подтверждённо работает; SMTP — на случай, если блокировка снимется или
/// добавится провайдер на другом порту/сети).
/// </summary>
public class CompositeEmailSender(
    IReadOnlyList<IEmailSender> senders, ILogger<CompositeEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, EmailBody body, CancellationToken ct = default)
    {
        if (senders.Count == 0)
            throw new InvalidOperationException("CompositeEmailSender зарегистрирован с пустым списком каналов.");

        List<Exception>? failures = null;
        foreach (var sender in senders)
        {
            try
            {
                await sender.SendAsync(to, subject, body, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Email-канал {Sender}: сбой отправки — переключение на следующий", sender.GetType().Name);
                (failures ??= []).Add(ex);
            }
        }

        throw new InvalidOperationException(
            $"Не удалось отправить письмо: все {senders.Count} email-канала(ов) недоступны.",
            failures is null ? null : new AggregateException(failures));
    }
}
