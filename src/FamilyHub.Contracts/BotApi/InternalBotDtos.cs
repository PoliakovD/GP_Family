using System.Text.Json.Serialization;

namespace FamilyHub.Contracts.BotApi;

// DTO проводного контракта /internal/bot/* между FamilyHub.TelegramBot (клиент) и
// FamilyHub.Api (сервер). Энумы объявлены здесь, а не переиспользуют
// FamilyHub.Domain.Enums.RedeemResult / FamilyHub.Api.Features.Auth.LinkTelegramResult —
// внутренний рефакторинг домена не должен молча поменять формат на проводе между двумя
// независимо деплоящимися процессами. [JsonConverter(JsonStringEnumConverter)] — прямо на типе,
// а не через глобальный AddJsonOptions в Program.cs (который затронул бы вообще все /api-ответы):
// значения читаются строками в логах/curl-отладке /internal/bot/*, не integer-кодами.

public record ResolveUserRequest(long TelegramId);

public record ResolveUserResponse(bool IsLinked);

public record RedeemInviteRequest(string Code, long TelegramId);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BotRedeemOutcome
{
    NotLinked, // TelegramId ещё не привязан ни к одному User — lookup-only, инвайт не трогаем
    NotFound,
    Revoked,
    Expired,
    Exhausted,
    NotForYou,
    AlreadyMember,
    Joined,
    PendingApproval,
}

public record RedeemInviteResponse(BotRedeemOutcome Outcome);

public record PeekLinkRequest(string Code);

public record PeekLinkResponse(bool Found, string? MaskedEmail);

public record ConfirmLinkRequest(string Code, long TelegramId, string? Username);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BotLinkOutcome
{
    Linked,
    Merged,
    TelegramAlreadyOnThisAccount,
    InvalidCode,
}

public record ConfirmLinkResponse(BotLinkOutcome Outcome);
