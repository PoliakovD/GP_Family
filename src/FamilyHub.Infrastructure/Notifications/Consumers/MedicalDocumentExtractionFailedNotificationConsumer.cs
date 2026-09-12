using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using MassTransit;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Уведомляет владельца мед-записи, что распознавание вложения окончательно не удалось (зеркало
/// MedicalDocumentExtractedNotificationConsumer на неудачный исход). FamilyId = null — та же
/// причина, что у consumer'а успеха: медзапись — персональный ресурс без семейного контекста
/// (null, не Guid.Empty — см. class doc Notification.FamilyId/MedicalDocumentExtractedNotificationConsumer).
/// </summary>
public class MedicalDocumentExtractionFailedNotificationConsumer(NotificationSendingService notifications)
    : IConsumer<MedicalDocumentExtractionFailedEvent>
{
    public async Task Consume(ConsumeContext<MedicalDocumentExtractionFailedEvent> context)
    {
        var e = context.Message;

        await notifications.NotifyAsync(
            [e.OwnerUserId],
            null,
            NotificationType.MedicalDocumentExtractionFailed,
            "Не удалось распознать документ",
            $"{e.Reason} Откройте запись и попробуйте распознать ещё раз.",
            relatedEntityId: e.RecordId,
            dedupKeyFor: _ => $"document-extraction-failed:{e.JobId}",
            ct: context.CancellationToken,
            relatedEntityKind: e.IsDoctorVisit ? NotificationRelatedKind.MedicalRecordVisit : NotificationRelatedKind.MedicalRecordAnalysis);
    }
}
