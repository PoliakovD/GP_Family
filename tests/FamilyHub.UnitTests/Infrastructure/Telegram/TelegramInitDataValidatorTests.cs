using System.Security.Cryptography;
using System.Text;
using FamilyHub.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Telegram;

public class TelegramInitDataValidatorTests
{
    private const string BotToken = "123456:test-bot-token";

    private static TelegramInitDataValidator CreateSut(TimeSpan? maxAge = null) =>
        new(Options.Create(new TelegramOptions { BotToken = BotToken, MaxInitDataAge = maxAge ?? TimeSpan.FromHours(24) }));

    /// <summary>
    /// Воспроизводит официальный алгоритм подписи initData (см. докстрингу самого валидатора),
    /// чтобы тест мог сконструировать ВАЛИДНУЮ строку — иначе позитивный кейс непроверяем.
    /// </summary>
    private static string BuildSignedInitData(long userId, string? firstName, string? lastName, DateTimeOffset authDate, string botToken = BotToken)
    {
        var userJson = $"{{\"id\":{userId},\"first_name\":\"{firstName}\",\"last_name\":\"{lastName}\"}}";
        var fields = new Dictionary<string, string>
        {
            ["auth_date"] = authDate.ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAA-test-query-id",
            ["user"] = userJson,
        };

        var dataCheckString = string.Join('\n', fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var secretKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString)));

        var query = fields.ToDictionary(kv => kv.Key, kv => Uri.EscapeDataString(kv.Value));
        query["hash"] = hash;
        return string.Join('&', query.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    [Fact]
    public void Validate_CorrectlySignedInitData_ReturnsParsedResult()
    {
        var initData = BuildSignedInitData(42, "Ada", "Lovelace", DateTimeOffset.UtcNow);

        var result = CreateSut().Validate(initData);

        result.Should().NotBeNull();
        result!.TelegramId.Should().Be(42);
        result.DisplayName.Should().Be("Ada Lovelace");
    }

    [Fact]
    public void Validate_TamperedHash_ReturnsNull()
    {
        var initData = BuildSignedInitData(42, "Ada", "Lovelace", DateTimeOffset.UtcNow);
        var tampered = initData[..^4] + "dead";

        var result = CreateSut().Validate(tampered);

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_TamperedUserId_ReturnsNull()
    {
        var initData = BuildSignedInitData(42, "Ada", "Lovelace", DateTimeOffset.UtcNow);
        var tampered = initData.Replace("id%22%3A42", "id%22%3A999");

        var result = CreateSut().Validate(tampered);

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_ExpiredAuthDate_ReturnsNull()
    {
        var initData = BuildSignedInitData(42, "Ada", "Lovelace", DateTimeOffset.UtcNow.AddHours(-48));

        var result = CreateSut(maxAge: TimeSpan.FromHours(24)).Validate(initData);

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_EmptyBotToken_ReturnsNull()
    {
        var initData = BuildSignedInitData(42, "Ada", "Lovelace", DateTimeOffset.UtcNow);
        var sut = new TelegramInitDataValidator(Options.Create(new TelegramOptions { BotToken = "" }));

        var result = sut.Validate(initData);

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_EmptyInitData_ReturnsNull()
    {
        var result = CreateSut().Validate("");

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_MissingHash_ReturnsNull()
    {
        var result = CreateSut().Validate("auth_date=123&user=%7B%7D");

        result.Should().BeNull();
    }

    [Fact]
    public void Validate_MissingUser_ReturnsNull()
    {
        // Подписываем data-check-string без поля "user" — hash валиден, но user отсутствует.
        var fields = new Dictionary<string, string> { ["auth_date"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() };
        var dataCheckString = string.Join('\n', fields.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var secretKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(BotToken));
        var hash = Convert.ToHexStringLower(HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString)));

        var result = CreateSut().Validate($"auth_date={fields["auth_date"]}&hash={hash}");

        result.Should().BeNull();
    }
}
