using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// Отправка через список SMTP-провайдеров с автопереключением (задача 2.5): провайдеры
/// пробуются по порядку; сбой соединения/протокола или исчерпание суточного лимита →
/// следующий. Суточный счётчик — в памяти процесса (single-instance деплой; при
/// горизонтальном масштабировании счётчик станет консервативной нижней границей —
/// зафиксировано в ADR-0001).
/// </summary>
public class MailKitSmtpEmailSender(
    ISmtpTransport transport,
    IOptions<EmailOptions> options,
    ILogger<MailKitSmtpEmailSender> logger) : IEmailSender
{
    /// <summary>Счётчик отправок за календарные сутки (UTC): ключ — имя провайдера.</summary>
    private readonly ConcurrentDictionary<string, (DateOnly Day, int Count)> _dailyCounts = new();

    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        var providers = options.Value.Providers;
        if (providers.Count == 0)
            throw new InvalidOperationException("Email:Providers пуст — MailKitSmtpEmailSender не должен быть зарегистрирован.");

        List<Exception>? failures = null;
        foreach (var provider in providers)
        {
            if (IsDailyLimitReached(provider))
            {
                logger.LogWarning("SMTP {Provider}: суточный лимит {Limit} исчерпан — переключение", provider.Name, provider.DailyLimit);
                continue;
            }

            try
            {
                await transport.SendAsync(provider, to, subject, textBody, ct);
                IncrementDailyCount(provider);
                logger.LogDebug("Письмо отправлено через SMTP-провайдера {Provider}", provider.Name);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Любой сбой провайдера (соединение, auth, протокол) → пробуем следующего.
                logger.LogWarning(ex, "SMTP {Provider}: сбой отправки — переключение на следующего", provider.Name);
                (failures ??= []).Add(ex);
            }
        }

        throw new InvalidOperationException(
            $"Не удалось отправить письмо: все {providers.Count} SMTP-провайдер(ов) недоступны или исчерпали лимит.",
            failures is null ? null : new AggregateException(failures));
    }

    private bool IsDailyLimitReached(SmtpProviderOptions provider)
    {
        if (provider.DailyLimit is not { } limit) return false;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return _dailyCounts.TryGetValue(provider.Name, out var entry)
            && entry.Day == today
            && entry.Count >= limit;
    }

    private void IncrementDailyCount(SmtpProviderOptions provider)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _dailyCounts.AddOrUpdate(
            provider.Name,
            _ => (today, 1),
            (_, entry) => entry.Day == today ? (entry.Day, entry.Count + 1) : (today, 1));
    }
}
