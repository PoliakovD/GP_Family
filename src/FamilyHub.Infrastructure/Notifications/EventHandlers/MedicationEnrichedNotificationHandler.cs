using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using MediatR;

namespace FamilyHub.Infrastructure.Notifications.EventHandlers;

/// <summary>
/// Уведомляет пользователя, чьё сохранение медикамента запустило обогащение (этап 4), что
/// справочник пополнен. Только его — не всю семью, как MedicationExpiringNotificationHandler:
/// дедуп задач конвейера по NormalizedName означает, что при параллельном сохранении того же
/// препарата в другой семье вторая задача не создаётся вовсе (см. EnrichmentRequestService).
/// </summary>
public class MedicationEnrichedNotificationHandler(NotificationSendingService notifications)
    : INotificationHandler<MedicationEnrichedEvent>
{
    public async Task Handle(MedicationEnrichedEvent notification, CancellationToken cancellationToken)
    {
        await notifications.NotifyAsync(
            [notification.RequestedByUserId],
            notification.FamilyId,
            NotificationType.MedicationEnriched,
            $"Справочник пополнен: {notification.DisplayName}",
            $"Мы нашли и добавили информацию о препарате «{notification.DisplayName}» в общий справочник.",
            relatedEntityId: notification.KbId,
            dedupKeyFor: _ => $"kb-enriched:{notification.JobId}",
            ct: cancellationToken);
    }
}
