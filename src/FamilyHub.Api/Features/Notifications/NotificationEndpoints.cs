using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Notifications;

public record NotificationPreferenceDto(NotificationType Type, bool PushEnabled, bool TelegramEnabled);

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();

        group.MapGet("/", async (bool? unreadOnly, NotificationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetMyNotificationsAsync(currentUser.UserId, unreadOnly ?? false, ct)));

        // Редизайн v2 — только счётчик для бейджа сайдбара/таба «Ещё» (опрашивается на каждой
        // навигации, см. NotificationStateService на фронте) — не тянуть полный список ради числа.
        group.MapGet("/unread-count", async (NotificationService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(new { count = await service.GetUnreadCountAsync(currentUser.UserId, ct) }));

        group.MapPost("/{id:guid}/read", async (Guid id, NotificationService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.MarkReadAsync(id, currentUser.UserId, ct);
            // Чужое или несуществующее оповещение — 404, без различия (не подтверждаем существование чужих).
            return result == MarkReadResult.Success ? Results.NoContent() : Results.NotFound();
        });

        // Предпочтения доставки по типу (вкладка «Настройки → Уведомления»). GET всегда отдаёт
        // ПОЛНУЮ матрицу — все значения NotificationType, включая не сохранённые (дефолт true/true,
        // разреженное хранение см. UserNotificationPreference) — чтобы фронт не достраивал дефолты сам.
        group.MapGet("/preferences", async (ICurrentUser currentUser, AppDbContext db, CancellationToken ct) =>
        {
            var saved = await db.Set<UserNotificationPreference>().AsNoTracking()
                .Where(p => p.UserId == currentUser.UserId)
                .ToDictionaryAsync(p => p.Type, ct);

            var all = Enum.GetValues<NotificationType>().Select(type => saved.TryGetValue(type, out var pref)
                ? new NotificationPreferenceDto(type, pref.PushEnabled, pref.TelegramEnabled)
                : new NotificationPreferenceDto(type, true, true));

            return Results.Ok(all);
        });

        group.MapPut("/preferences", async (
            NotificationPreferenceDto[] request, ICurrentUser currentUser, AppDbContext db, CancellationToken ct) =>
        {
            var existing = await db.Set<UserNotificationPreference>()
                .Where(p => p.UserId == currentUser.UserId)
                .ToDictionaryAsync(p => p.Type, ct);

            foreach (var dto in request)
            {
                // Дефолт "всё включено" не хранится вовсе — разреженное хранение (см. сущность).
                if (dto.PushEnabled && dto.TelegramEnabled)
                {
                    if (existing.TryGetValue(dto.Type, out var toRemove))
                        db.Remove(toRemove);
                    continue;
                }

                if (existing.TryGetValue(dto.Type, out var pref))
                {
                    pref.PushEnabled = dto.PushEnabled;
                    pref.TelegramEnabled = dto.TelegramEnabled;
                }
                else
                {
                    db.Add(new UserNotificationPreference
                    {
                        UserId = currentUser.UserId,
                        Type = dto.Type,
                        PushEnabled = dto.PushEnabled,
                        TelegramEnabled = dto.TelegramEnabled,
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
