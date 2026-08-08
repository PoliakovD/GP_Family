using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Consumers;

/// <summary>
/// Критичный инвариант этапа 1: пользователь покинул семью → его FamilyMedicalShare для
/// этой семьи отзываются. Modules.Medical реагирует на событие, не будучи связан с Core.
/// ExecuteDelete идемпотентен — повторная доставка события просто не удалит ничего.
/// Отдельный потребитель на отдельном receive endpoint от UserLeftFamilyNotificationConsumer
/// (Infrastructure) — падение одного больше не может помешать другому (ADR-0006).
/// </summary>
public class UserLeftFamilyMedicalCleanupConsumer(
    AppDbContext db,
    ILogger<UserLeftFamilyMedicalCleanupConsumer> logger) : IConsumer<UserLeftFamilyEvent>
{
    public async Task Consume(ConsumeContext<UserLeftFamilyEvent> context)
    {
        var notification = context.Message;

        var removed = await db.FamilyMedicalShares
            .Where(s => s.FamilyId == notification.FamilyId && s.OwnerUserId == notification.UserId)
            .ExecuteDeleteAsync(context.CancellationToken);

        if (removed > 0)
            logger.LogInformation(
                "Отозвано {Count} FamilyMedicalShare пользователя {UserId} для семьи {FamilyId} (сообщение {MessageId})",
                removed, notification.UserId, notification.FamilyId, context.MessageId);
    }
}
