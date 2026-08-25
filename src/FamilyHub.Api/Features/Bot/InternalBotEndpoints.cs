using FamilyHub.Api.Features.Auth;
using FamilyHub.Api.Features.Invites;
using FamilyHub.Contracts.BotApi;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Bot;

/// <summary>
/// Внутренний HTTP-контракт для FamilyHub.TelegramBot (этап выноса бота, см. ADR-0008) — обёртка
/// БЕЗ бизнес-логики поверх уже существующих IUserProvisioningService/InviteService/
/// TelegramLinkService: сам бот больше не имеет доступа к БД (см. InternalBotAuthFilter), поэтому
/// каждая операция, которую раньше TelegramUpdateHandler делал прямым in-process вызовом, стала
/// HTTP-запросом сюда. Все методы — POST (не GET), чтобы коды/telegramId не оседали в access-логах
/// Caddy. Резолв "telegramId → userId" остаётся целиком на стороне Api (redeem принимает
/// telegramId, а не userId) — тот же lookup-only принцип, что раньше жил в TelegramUpdateHandler.
/// </summary>
public static class InternalBotEndpoints
{
    public static void MapInternalBotEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/bot")
            .AddEndpointFilter<InternalBotAuthFilter>()
            .AllowAnonymous(); // обязателен: FallbackPolicy в Program.cs требует аутентификации по умолчанию

        group.MapGet("/ping", () => Results.Ok());

        group.MapPost("/users/resolve", async (
            ResolveUserRequest request, IUserProvisioningService provisioning, CancellationToken ct) =>
        {
            var userId = await provisioning.GetUserIdByTelegramIdAsync(request.TelegramId, ct);
            return Results.Ok(new ResolveUserResponse(userId is not null));
        });

        group.MapPost("/invites/redeem", async (
            RedeemInviteRequest request, IUserProvisioningService provisioning, InviteService invites, CancellationToken ct) =>
        {
            var userId = await provisioning.GetUserIdByTelegramIdAsync(request.TelegramId, ct);
            if (userId is null)
                return Results.Ok(new RedeemInviteResponse(BotRedeemOutcome.NotLinked));

            var result = await invites.RedeemInviteAsync(request.Code, userId.Value, ct);
            return Results.Ok(new RedeemInviteResponse(MapRedeemOutcome(result)));
        });

        group.MapPost("/telegram-link/peek", async (
            PeekLinkRequest request, TelegramLinkService links, CancellationToken ct) =>
        {
            var peek = await links.PeekAsync(request.Code, ct);
            return Results.Ok(peek is null
                ? new PeekLinkResponse(Found: false, MaskedEmail: null)
                : new PeekLinkResponse(Found: true, peek.MaskedEmail));
        });

        group.MapPost("/telegram-link/confirm", async (
            ConfirmLinkRequest request, TelegramLinkService links, CancellationToken ct) =>
        {
            var result = await links.ConfirmAsync(request.Code, request.TelegramId, request.Username, ct);
            return Results.Ok(new ConfirmLinkResponse(MapLinkOutcome(result)));
        });
    }

    private static BotRedeemOutcome MapRedeemOutcome(RedeemResult result) => result switch
    {
        RedeemResult.NotFound => BotRedeemOutcome.NotFound,
        RedeemResult.Revoked => BotRedeemOutcome.Revoked,
        RedeemResult.Expired => BotRedeemOutcome.Expired,
        RedeemResult.Exhausted => BotRedeemOutcome.Exhausted,
        RedeemResult.NotForYou => BotRedeemOutcome.NotForYou,
        RedeemResult.AlreadyMember => BotRedeemOutcome.AlreadyMember,
        RedeemResult.Joined => BotRedeemOutcome.Joined,
        RedeemResult.PendingApproval => BotRedeemOutcome.PendingApproval,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    private static BotLinkOutcome MapLinkOutcome(LinkTelegramResult result) => result switch
    {
        LinkTelegramResult.Linked => BotLinkOutcome.Linked,
        LinkTelegramResult.Merged => BotLinkOutcome.Merged,
        LinkTelegramResult.TelegramAlreadyOnThisAccount => BotLinkOutcome.TelegramAlreadyOnThisAccount,
        LinkTelegramResult.InvalidCode => BotLinkOutcome.InvalidCode,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };
}
