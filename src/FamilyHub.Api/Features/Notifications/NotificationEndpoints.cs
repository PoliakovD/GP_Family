using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Notifications;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();

        group.MapGet("/", async (bool? unreadOnly, NotificationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetMyNotificationsAsync(currentUser.UserId, unreadOnly ?? false, ct)));

        group.MapPost("/{id:guid}/read", async (Guid id, NotificationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.MarkReadAsync(id, currentUser.UserId, ct);
            // Чужое или несуществующее оповещение — 404, без различия (не подтверждаем существование чужих).
            return result == MarkReadResult.Success ? Results.NoContent() : Results.NotFound();
        });
    }
}
