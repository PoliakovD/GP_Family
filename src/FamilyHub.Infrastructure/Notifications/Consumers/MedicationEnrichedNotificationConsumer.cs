using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using MassTransit;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Уведомляет пользователя, чьё сохранение медикамента запустило обогащение (этап 4), что
/// справочник пополнен. Только его — не всю семью, как MedicationExpiringNotificationConsumer:
/// дедуп задач конвейера по NormalizedName означает, что при параллельном сохранении того же
/// препарата в другой семье вторая задача не создаётся вовсе (см. EnrichmentRequestService).
/// </summary>
public class MedicationEnrichedNotificationConsumer(NotificationSendingService notifications)
    : IConsumer<MedicationEnrichedEvent>
{
    public async Task Consume(ConsumeContext<MedicationEnrichedEvent> context)
    {
        var notification = context.Message;

        await notifications.NotifyAsync(
            [notification.RequestedByUserId],
            notification.FamilyId,
            NotificationType.MedicationEnriched,
            $"Справочник пополнен: {notification.DisplayName}",
            $"Мы нашли и добавили информацию о препарате «{notification.DisplayName}» в общий справочник.",
            relatedEntityId: notification.KbId,
            dedupKeyFor: _ => $"kb-enriched:{notification.JobId}",
            ct: context.CancellationToken);
    }
}
