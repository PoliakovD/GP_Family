using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using MassTransit;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Уведомляет пользователя, чьё сохранение медикамента запустило обогащение, что оно окончательно
/// не удалось — зеркало MedicationEnrichedNotificationConsumer на неудачный исход. Только его же
/// (не всю семью) — та же причина, что у consumer'а успеха.
/// </summary>
public class MedicationEnrichmentFailedNotificationConsumer(NotificationSendingService notifications)
    : IConsumer<MedicationEnrichmentFailedEvent>
{
    public async Task Consume(ConsumeContext<MedicationEnrichmentFailedEvent> context)
    {
        var e = context.Message;

        await notifications.NotifyAsync(
            [e.RequestedByUserId],
            e.FamilyId,
            NotificationType.MedicationEnrichmentFailed,
            $"Не удалось найти информацию о препарате «{e.DisplayName}»",
            "Мы поискали в открытых источниках, но не нашли описание этого препарата.",
            // MedkitId — null, если медикамент уже удалён к моменту отказа (справочно, не FK —
            // см. MedicationEnrichmentJob.MedicationId); тогда карточка остаётся некликабельной.
            relatedEntityId: e.MedkitId ?? Guid.Empty,
            dedupKeyFor: _ => $"medication-enrichment-failed:{e.JobId}",
            ct: context.CancellationToken,
            relatedEntityKind: e.MedkitId is not null ? NotificationRelatedKind.Medkit : null);
    }
}
