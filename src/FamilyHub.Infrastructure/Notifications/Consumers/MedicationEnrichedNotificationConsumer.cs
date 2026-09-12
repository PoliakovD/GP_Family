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
            // RelatedEntityId раньше был KbId — сам справочник не привязан к семье и никуда не
            // ведёт на фронте, поэтому под клик-через (§3 плана) здесь MedkitId: аптечка, из
            // которой запущено обогащение, когда она ещё существует (см.
            // MedicationEnrichmentProcessor.ResolveMedkitIdAsync). Guid.Empty без RelatedEntityKind —
            // карточка остаётся некликабельной (ничего не сломано — до этой задачи relatedEntityId
            // никем на фронте не читался, см. AppNotification).
            relatedEntityId: notification.MedkitId ?? Guid.Empty,
            dedupKeyFor: _ => $"kb-enriched:{notification.JobId}",
            ct: context.CancellationToken,
            relatedEntityKind: notification.MedkitId is not null ? NotificationRelatedKind.Medkit : null);
    }
}
