using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using MassTransit;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Уведомляет владельца мед-записи, что распознавание вложения завершено (ветка medicalrecords).
/// FamilyId = Guid.Empty: медзапись — персональный ресурс без семейного контекста (в отличие от
/// MedicalRecordSharedNotificationConsumer, где шаринг именно семье). Notification.FamilyId нигде
/// не читается обратно ни в одном эндпоинте (проверено — только пишется), поэтому пустой Guid
/// здесь безопасен и не требует отдельной миграции на nullable-колонку ради одного этого случая.
/// </summary>
public class MedicalDocumentExtractedNotificationConsumer(NotificationSendingService notifications)
    : IConsumer<MedicalDocumentExtractedEvent>
{
    public async Task Consume(ConsumeContext<MedicalDocumentExtractedEvent> context)
    {
        var e = context.Message;

        var (title, body) = e.IndicatorCount > 0
            ? ($"Анализ распознан: {e.IndicatorCount} показателей",
               e.DeviationCount > 0
                   ? $"Найдено отклонений от нормы: {e.DeviationCount}. Откройте запись, чтобы посмотреть подробности."
                   : "Все показатели в пределах нормы.")
            : ("Документ распознан", "Откройте запись, чтобы посмотреть результат.");

        await notifications.NotifyAsync(
            [e.OwnerUserId],
            Guid.Empty,
            NotificationType.MedicalDocumentExtracted,
            title,
            body,
            relatedEntityId: e.RecordId,
            dedupKeyFor: _ => $"document-extracted:{e.JobId}",
            ct: context.CancellationToken);
    }
}
