using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using MassTransit;

namespace FamilyHub.Infrastructure.Notifications.Consumers;

/// <summary>
/// Уведомляет владельца мед-записи, что распознавание вложения завершено (ветка medicalrecords).
/// FamilyId = null: медзапись — персональный ресурс без семейного контекста (в отличие от
/// MedicalRecordSharedNotificationConsumer, где шаринг именно семье). null, не Guid.Empty —
/// FamilyId — FK на Families (см. NotificationConfiguration), а Guid.Empty на Postgres реально
/// бросал бы FK violation при вставке (никакой семьи с таким Id не существует), молча
/// проглатываемый catch-блоком NotificationSendingService.AddIfNewAsync как будто это была гонка
/// дедупа — это уведомление никогда не создавалось бы (баг, найденный при написании теста на
/// эту задачу — исправлено, см. Notification.FamilyId).
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
            null,
            NotificationType.MedicalDocumentExtracted,
            title,
            body,
            relatedEntityId: e.RecordId,
            dedupKeyFor: _ => $"document-extracted:{e.JobId}",
            ct: context.CancellationToken,
            relatedEntityKind: e.IsDoctorVisit ? NotificationRelatedKind.MedicalRecordVisit : NotificationRelatedKind.MedicalRecordAnalysis);
    }
}
