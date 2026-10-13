using System.Security.Cryptography;
using System.Text;
using FamilyHub.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Auth;

/// <summary>
/// Тикеты входа в SignalR-хаб (ADR-0019 п. 3): <c>{userId:N}.{expiresUnix}.{hmac}</c>, живут
/// <see cref="Ttl"/>. Выдаются только уже аутентифицированному запросу (cookie+CSRF или initData), поэтому
/// сами по себе ничего не расширяют — лишь переносят «кто это» туда, куда браузер не может прислать ни
/// заголовок tma, ни (надёжно) cookie. Подпись — ключами ссылок на вложения (та же связка с ротацией,
/// ADR-0009) с префиксом <c>hub:</c> в payload: по формату он не совпадёт ни с одной подписью ссылки.
/// </summary>
public class HubTicketService(IOptions<AttachmentDownloadOptions> options)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public (string Ticket, DateTime ExpiresAt) Create(Guid userId)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(Ttl);
        var expiresUnix = expiresAt.ToUnixTimeSeconds();
        var signature = Sign(options.Value.DownloadSigningKey, userId, expiresUnix);
        return ($"{userId:N}.{expiresUnix}.{signature}", expiresAt.UtcDateTime);
    }

    /// <summary>Id пользователя, если тикет цел и не истёк; иначе null.</summary>
    public Guid? Validate(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;

        var parts = ticket.Split('.');
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var userId)
            || !long.TryParse(parts[1], out var expiresUnix))
            return null;

        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresUnix) return null;

        var signatureBytes = Encoding.UTF8.GetBytes(parts[2]);
        foreach (var key in AllKeys())
        {
            var expected = Encoding.UTF8.GetBytes(Sign(key, userId, expiresUnix));
            if (CryptographicOperations.FixedTimeEquals(expected, signatureBytes)) return userId;
        }
        return null;
    }

    private IEnumerable<string> AllKeys()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.DownloadSigningKey)) yield return options.Value.DownloadSigningKey;
        foreach (var key in options.Value.PreviousSigningKeys)
        {
            if (!string.IsNullOrWhiteSpace(key)) yield return key;
        }
    }

    private static string Sign(string key, Guid userId, long expiresUnix)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Attachments:DownloadSigningKey не задан — выдача тикетов хаба невозможна.");

        var payload = $"hub:{userId:N}:{expiresUnix}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));
    }
}
