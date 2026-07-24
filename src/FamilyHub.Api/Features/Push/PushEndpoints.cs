using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Push;

public static class PushEndpoints
{
    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push").RequireAuthorization();

        // null — Web Push не настроен (нет VAPID) — фронт скрывает тумблер подписки в Настройках.
        group.MapGet("/vapid-public-key", (PushSubscriptionService service) =>
        {
            var key = service.GetVapidPublicKey();
            return key is null ? Results.NotFound() : Results.Ok(new VapidPublicKeyResponse(key));
        });

        group.MapPost("/subscribe", async (
            SubscribePushRequest request, PushSubscriptionService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            await service.SubscribeAsync(currentUser.UserId, request.Endpoint, request.P256dh, request.Auth, ct);
            return Results.NoContent();
        });

        group.MapPost("/unsubscribe", async (
            UnsubscribePushRequest request, PushSubscriptionService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var removed = await service.UnsubscribeAsync(currentUser.UserId, request.Endpoint, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });
    }
}
