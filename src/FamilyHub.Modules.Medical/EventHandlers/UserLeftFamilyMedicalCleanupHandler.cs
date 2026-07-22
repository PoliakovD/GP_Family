using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.EventHandlers;

/// <summary>
/// Критичный инвариант этапа 1: пользователь покинул семью → его FamilyMedicalShare для
/// этой семьи отзываются. Раньше чистку делал MembershipService напрямую; теперь Medical
/// реагирует на событие, не будучи связан с Core. ExecuteDelete идемпотентен —
/// повторная доставка события просто не удалит ничего.
/// </summary>
public class UserLeftFamilyMedicalCleanupHandler(
    AppDbContext db,
    ILogger<UserLeftFamilyMedicalCleanupHandler> logger) : INotificationHandler<UserLeftFamilyEvent>
{
    public async Task Handle(UserLeftFamilyEvent notification, CancellationToken cancellationToken)
    {
        var removed = await db.FamilyMedicalShares
            .Where(s => s.FamilyId == notification.FamilyId && s.OwnerUserId == notification.UserId)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
            logger.LogInformation(
                "Отозвано {Count} FamilyMedicalShare пользователя {UserId} для семьи {FamilyId} (событие {EventId})",
                removed, notification.UserId, notification.FamilyId, notification.EventId);
    }
}
