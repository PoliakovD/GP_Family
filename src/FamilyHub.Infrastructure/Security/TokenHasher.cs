using System.Security.Cryptography;
using System.Text;

namespace FamilyHub.Infrastructure.Security;

/// <summary>
/// SHA-256 (hex) хеширование одноразовых/секретных токенов, которые хранятся только по
/// хешу (сам токен нигде не сохраняется): email-коды, Telegram-link-коды, refresh-токены.
/// Раньше дублировалось приватным static-методом в PwaAuthService/TelegramLinkService.
/// </summary>
public static class TokenHasher
{
    public static string Hash(string rawValue) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawValue)));
}
