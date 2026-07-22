using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FamilyHub.Infrastructure.Email;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Тестовый IEmailSender: перехватывает письма в память, чтобы интеграционные тесты могли
/// достать код подтверждения (в проде — SMTP, в dev — лог; тестам нужен программный доступ).
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, string> _lastBodyByEmail = new();

    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        _lastBodyByEmail[to] = textBody;
        return Task.CompletedTask;
    }

    /// <summary>Последний шестизначный код, отправленный на адрес; null — писем не было.</summary>
    public string? LastCodeFor(string email) =>
        _lastBodyByEmail.TryGetValue(email, out var body) ? Regex.Match(body, @"\d{6}").Value : null;
}
