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

        // Идемпотентно: клиенту важен только конечный результат "подписки больше нет", а не то,
        // кто её убрал — тот же браузер сейчас или WebPushNotificationSender ранее при 404/410
        // от push-релея (см. его SendAsync). 404 здесь означал бы "уже отписан", а не ошибку —
        // раньше это ломало фронт (PushNotificationService.unsubscribe() падал ДО локальной
        // отписки от SW, тумблер застревал "включённым"). 200 — запись правда была и её удалили
        // сейчас, 204 — её и так уже не было; оба случая успешны.
        group.MapPost("/unsubscribe", async (
            UnsubscribePushRequest request, PushSubscriptionService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var removed = await service.UnsubscribeAsync(currentUser.UserId, request.Endpoint, ct);
            return removed ? Results.Ok() : Results.NoContent();
        });
    }
}
