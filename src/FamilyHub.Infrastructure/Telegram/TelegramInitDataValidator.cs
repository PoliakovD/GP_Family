using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Telegram;

/// <summary>
/// Реальная HMAC-валидация Telegram Mini App initData по официальному алгоритму:
/// https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app
///
/// secret_key = HMAC_SHA256(key="WebAppData", data=botToken)
/// data_check_hash = HEX(HMAC_SHA256(key=secret_key, data=data_check_string))
/// где data_check_string — все поля initData (кроме hash), сортированные по ключу,
/// в формате "key=value", соединённые '\n'.
///
/// Делается ПЕРВЫМ, до бизнес-логики: без этого любой подделает Telegram ID.
/// </summary>
public class TelegramInitDataValidator(IOptions<TelegramOptions> options) : ITelegramInitDataValidator
{
    private static readonly byte[] WebAppDataKey = Encoding.UTF8.GetBytes("WebAppData");

    public TelegramInitDataResult? Validate(string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
            return null;

        var botToken = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(botToken))
            return null; // не сконфигурирован токен бота — отказываем, а не пропускаем

        var pairs = HttpUtility.ParseQueryString(initData);
        var receivedHash = pairs["hash"];
        if (string.IsNullOrEmpty(receivedHash))
            return null;

        var dataCheckString = pairs.AllKeys
            .Where(k => k is not null && k != "hash")
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => $"{k}={pairs[k]}")
            .Aggregate(new StringBuilder(), (sb, line) => (sb.Length == 0 ? sb : sb.Append('\n')).Append(line))
            .ToString();

        var secretKey = HMACSHA256.HashData(WebAppDataKey, Encoding.UTF8.GetBytes(botToken));
        var computedHash = HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString));
        var computedHashHex = Convert.ToHexStringLower(computedHash);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedHashHex), Encoding.UTF8.GetBytes(receivedHash)))
            return null;

        if (long.TryParse(pairs["auth_date"], out var authDateUnix))
        {
            var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateUnix);
            if (DateTimeOffset.UtcNow - authDate > options.Value.MaxInitDataAge)
                return null; // просрочена
        }

        var userJson = pairs["user"];
        if (string.IsNullOrEmpty(userJson))
            return null;

        TelegramUserDto? user;
        try
        {
            user = JsonSerializer.Deserialize<TelegramUserDto>(userJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (user is null || user.Id == 0)
            return null;

        var displayName = string.Join(' ', new[] { user.FirstName, user.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return new TelegramInitDataResult(user.Id, string.IsNullOrWhiteSpace(displayName) ? null : displayName);
    }

    private sealed class TelegramUserDto
    {
        // Без явного имени System.Text.Json матчит регистро-зависимо ("Id" != "id" из реального
        // Telegram-JSON) и Id всегда оставался бы 0, проваливая ЛЮБУЮ валидацию initData.
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public long Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("first_name")]
        public string? FirstName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("last_name")]
        public string? LastName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("username")]
        public string? Username { get; set; }
    }
}
