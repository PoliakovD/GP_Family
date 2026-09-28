using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Email;

/// <summary>
/// In-memory перехват писем, отправленных через <see cref="LoggingEmailSender"/> (то есть только
/// когда ни один реальный провайдер не настроен, см. EmailRegistration) — даёт e2e и локальной
/// разработке программный доступ к коду подтверждения без похода в почту/лог (реальный API-процесс,
/// не WebApplicationFactory — там для того же нужен CapturingEmailSender в тестовом DI). Зеркало
/// CapturingEmailSender (tests/FamilyHub.IntegrationTests) по дизайну — только код скана, а не общий
/// класс, так как классы живут в разных, не связанных ссылкой сборках. Публичный эндпоинт поверх
/// него — GET /dev/last-otp, гейт тот же DevTools:DevEndpointsEnabled, что и весь DevEndpoints.
/// </summary>
public sealed class DevEmailCapture
{
    private sealed record Message(string Body);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<Message>> _byEmail = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string to, string bodyText) =>
        _byEmail.GetOrAdd(to, _ => new ConcurrentQueue<Message>()).Enqueue(new Message(bodyText));

    /// <summary>Последний шестизначный код среди писем на адрес; null — такого письма не было.
    /// Сканирует назад — на один адрес могли уйти два разных письма (например, OTP привязки
    /// Telegram, потом временный пароль нового аккаунта), нужен именно код из последнего.</summary>
    public string? LastCodeFor(string email) =>
        _byEmail.TryGetValue(email, out var queue)
            ? queue.ToArray().Reverse().Select(m => Regex.Match(m.Body, @"\d{6}")).FirstOrDefault(m => m.Success)?.Value
            : null;
}
