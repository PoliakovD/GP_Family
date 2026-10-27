using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
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
public class TelegramInitDataValidator(IOptions<TelegramOptions> options, ILogger<TelegramInitDataValidator> logger) : ITelegramInitDataValidator
{
    private static readonly byte[] WebAppDataKey = Encoding.UTF8.GetBytes("WebAppData");

    /// <summary>Допуск на расхождение часов: auth_date «из будущего» больше чем на это — подделка
    /// или сломанные часы клиента, такие initData не принимаем (аудит security-audit-2026-10, M6).</summary>
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public TelegramInitDataResult? Validate(string initData) => Check(initData).Result;

    public TelegramInitDataCheck Check(string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
        {
            logger.LogDebug("Валидация initData: пустая строка");
            return TelegramInitDataCheck.Invalid;
        }

        var botToken = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(botToken))
        {
            logger.LogWarning("Валидация initData отклонена: не сконфигурирован Telegram:BotToken");
            return TelegramInitDataCheck.Invalid; // не сконфигурирован токен бота — отказываем, а не пропускаем
        }

        var pairs = HttpUtility.ParseQueryString(initData);
        var receivedHash = pairs["hash"];
        if (string.IsNullOrEmpty(receivedHash))
        {
            logger.LogDebug("Валидация initData отклонена: отсутствует поле hash");
            return TelegramInitDataCheck.Invalid;
        }

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
        {
            logger.LogWarning("Валидация initData отклонена: несовпадение HMAC-подписи");
            return TelegramInitDataCheck.Invalid;
        }

        // auth_date обязателен: раньше TTL проверялся только если поле распарсилось, и подписанные
        // данные без него (или с мусором в нём) действовали бессрочно (аудит security-audit-2026-10, M6).
        if (!long.TryParse(pairs["auth_date"], out var authDateUnix)
            || authDateUnix <= 0 || authDateUnix > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            logger.LogWarning("Валидация initData отклонена: отсутствует или некорректно поле auth_date");
            return TelegramInitDataCheck.Invalid;
        }

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authDateUnix);
        if (age < -MaxFutureSkew)
        {
            logger.LogWarning("Валидация initData отклонена: auth_date в будущем (на {Ahead})", -age);
            return TelegramInitDataCheck.Invalid;
        }
        if (age > options.Value.MaxInitDataAge)
        {
            logger.LogInformation(
                "Валидация initData отклонена: истекла (возраст {Age}, лимит {MaxAge})", age, options.Value.MaxInitDataAge);
            return TelegramInitDataCheck.Expired;
        }

        var userJson = pairs["user"];
        if (string.IsNullOrEmpty(userJson))
        {
            logger.LogWarning("Валидация initData отклонена: отсутствует поле user");
            return TelegramInitDataCheck.Invalid;
        }

        TelegramUserDto? user;
        try
        {
            user = JsonSerializer.Deserialize<TelegramUserDto>(userJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Валидация initData отклонена: не удалось разобрать поле user");
            return TelegramInitDataCheck.Invalid;
        }

        if (user is null || user.Id == 0)
        {
            logger.LogWarning("Валидация initData отклонена: некорректный или нулевой Telegram ID");
            return TelegramInitDataCheck.Invalid;
        }

        var displayName = string.Join(' ', new[] { user.FirstName, user.LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        logger.LogDebug("initData валидна для Telegram ID {TelegramId}", user.Id);

        return new TelegramInitDataCheck(new TelegramInitDataResult(
            user.Id,
            string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            string.IsNullOrWhiteSpace(user.Username) ? null : user.Username), TelegramInitDataFailure.None);
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
