using Microsoft.IdentityModel.Tokens;

namespace FamilyHub.Infrastructure.Auth.Jwt;

/// <summary>
/// Строит связку ключей подписи (активный + отставные) для <c>TokenValidationParameters
/// .IssuerSigningKeys</c> (ADR-0009). Вынесено из Program.cs отдельным статическим методом,
/// чтобы конфигурация связки (дубли keyId, битый base64) проверялась тем же fail-fast
/// принципом, что и <see cref="Security.EncryptionKeyRing"/>, и была тестируема независимо от
/// хоста. Валидация JWT не выбирает ключ ПО keyId — пробует каждый ключ связки по очереди
/// (.NET JwtBearer делает это сам); keyId (<c>kid</c> в заголовке токена) — только диагностика.
/// </summary>
public static class JwtSigningKeyRing
{
    public static IReadOnlyList<SecurityKey> Build(JwtOptions options)
    {
        var keys = new List<SecurityKey>
        {
            new SymmetricSecurityKey(DecodeKey(options.SigningKey, "Jwt:SigningKey"))
            {
                KeyId = options.ActiveKeyId,
            },
        };

        foreach (var entry in options.PreviousSigningKeys)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidOperationException(
                    "Jwt:PreviousSigningKeys содержит запись без Id (env Jwt__PreviousSigningKeys__N__Id).");
            if (keys.Any(k => k.KeyId == entry.Id))
                throw new InvalidOperationException(
                    $"Jwt:PreviousSigningKeys содержит дублирующийся keyId «{entry.Id}» — совпадает с " +
                    "ActiveKeyId или другой отставной записью.");

            keys.Add(new SymmetricSecurityKey(
                DecodeKey(entry.Material, $"Jwt:PreviousSigningKeys[{entry.Id}].Material"))
            {
                KeyId = entry.Id,
            });
        }

        return keys;
    }

    private static byte[] DecodeKey(string base64, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException($"{sourceName} не задан (env Jwt__SigningKey) — JWT-сессии PWA невозможны.");
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            // Без этой проверки ошибка проявлялась бы не при старте, а лениво — на первый же
            // входящий запрос, внутри IOptionsFactory для JwtBearer, и валила бы 500 АБСОЛЮТНО
            // любой запрос (включая AllowAnonymous — аутентификация пытается резолвить дефолтную
            // схему до authorization независимо от эндпоинта). Самый частый источник —
            // незаменённый плейсхолдер `Jwt__SigningKey=CHANGE_ME` из .env.example (не валиден
            // как Base64: недопустимый символ `_` и некорректная длина).
            throw new InvalidOperationException(
                $"{sourceName} задан, но не является корректной Base64-строкой — похоже на " +
                "незаменённый плейсхолдер из .env.example. Сгенерировать реальный ключ: " +
                "`openssl rand -base64 32` (или PowerShell: " +
                "[Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))).", ex);
        }
    }
}
