using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FamilyHub.Infrastructure.Email;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Тестовый IEmailSender: перехватывает письма в память, чтобы интеграционные тесты могли
/// достать код подтверждения/временный пароль (в проде — SMTP, в dev — лог; тестам нужен
/// программный доступ). Хранит ВСЕ письма на адрес, а не только последнее — TelegramBindingService
/// может отправить два разных письма на один адрес в рамках одного теста (OTP-код привязки, потом
/// временный пароль для нового аккаунта), и тестам нужно уметь и различить их по содержимому, и
/// проверить утверждение "второго письма не было" (существующий аккаунт с паролем — bind ничего
/// лишнего не шлёт).
/// </summary>
public sealed class CapturingEmailSender : IEmailSender
{
    /// <summary>
    /// Body — ВСЕГДА текстовая часть, никогда HTML: LastCodeFor ищет первый \d{6} в Body, а
    /// #004961 (accent-800) встречается в каждом email-шаблоне — если сюда попадёт HTML,
    /// хелпер начнёт возвращать "004961" вместо кода. Html — отдельное поле именно чтобы
    /// не было соблазна их перепутать/склеить.
    /// </summary>
    public sealed record Message(string To, string Subject, string Body, string? Html);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<Message>> _byEmail = new();

    public Task SendAsync(string to, string subject, EmailBody body, CancellationToken ct = default)
    {
        _byEmail.GetOrAdd(to, _ => new ConcurrentQueue<Message>()).Enqueue(new Message(to, subject, body.Text, body.Html));
        return Task.CompletedTask;
    }

    /// <summary>Все письма, отправленные на адрес, в порядке отправки; пусто — писем не было.</summary>
    public IReadOnlyList<Message> MessagesFor(string email) =>
        _byEmail.TryGetValue(email, out var queue) ? queue.ToArray() : [];

    /// <summary>Последний шестизначный код среди писем на адрес; null — такого письма не было.
    /// Сканирует НАЗАД, чтобы не спутать с более поздним письмом другого типа на тот же адрес
    /// (например, временным паролем, отправленным после OTP-кода привязки Telegram).</summary>
    public string? LastCodeFor(string email) =>
        MessagesFor(email).Reverse()
            .Select(m => Regex.Match(m.Body, @"\d{6}"))
            .FirstOrDefault(m => m.Success)?.Value;

    /// <summary>Последний временный пароль, отправленный TelegramBindingService на адрес;
    /// null — такого письма не было (существующий аккаунт с паролем ничего не получает).</summary>
    public string? LastTemporaryPasswordFor(string email) =>
        MessagesFor(email).Reverse()
            .Select(m => Regex.Match(m.Body, @"пароль для входа на сайте: (\S+)"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
}
