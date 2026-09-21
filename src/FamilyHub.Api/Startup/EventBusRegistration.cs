using FamilyHub.Contracts.Events;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Notifications.Consumers;
using FamilyHub.Modules.Medical;
using FamilyHub.Modules.Medical.Consumers;
using FamilyHub.Modules.Birthdays;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — событийная шина (MassTransit + EF Core
/// Outbox + Kafka Rider, ADR-0006/ADR-0007), без изменения поведения/порядка.
/// </summary>
public static class EventBusRegistration
{
    public static WebApplicationBuilder AddFamilyHubEventBus(this WebApplicationBuilder builder)
    {
        // Messaging:Kafka:Enabled=true (docker-compose/прод, дефолт для полного стека) — бизнес-потребители
        // подписаны на Kafka Rider (явный список ниже, composition root — единственное место, которому
        // позволено знать конкретные типы потребителей ИЗ ВСЕХ модулей сразу); false (dev-lite/юнит-тесты,
        // без Docker) — потребители сканом сборок на InMemory, как раньше. В обоих случаях сбой одного
        // потребителя не касается соседа — топология шины (свой receive endpoint/consumer group), не наш
        // код, как раньше у IsolatingLoggingPublisher.
        var kafkaConsumers = new KafkaConsumerRegistration[]
        {
            new(typeof(MedicalRecordSharedEvent), typeof(MedicalRecordSharedNotificationConsumer), "notifications-medical-record-shared"),
            new(typeof(UserLeftFamilyEvent), typeof(UserLeftFamilyNotificationConsumer), "notifications-user-left-family"),
            new(typeof(UserLeftFamilyEvent), typeof(UserLeftFamilyMedicalCleanupConsumer), "medical-user-left-family"),
            new(typeof(MemberApprovedEvent), typeof(MemberApprovedNotificationConsumer), "notifications-member-approved"),
            new(typeof(MedicationExpiringEvent), typeof(MedicationExpiringNotificationConsumer), "notifications-medication-expiring"),
            new(typeof(BirthdayApproachingEvent), typeof(BirthdayApproachingNotificationConsumer), "notifications-birthday-approaching"),
            new(typeof(MedicationEnrichedEvent), typeof(MedicationEnrichedNotificationConsumer), "notifications-medication-enriched"),
            new(typeof(MedicalDocumentExtractedEvent), typeof(MedicalDocumentExtractedNotificationConsumer), "notifications-medical-document-extracted"),
            new(typeof(MedicalDocumentExtractionFailedEvent), typeof(MedicalDocumentExtractionFailedNotificationConsumer), "notifications-medical-document-extraction-failed"),
            new(typeof(MedicationEnrichmentFailedEvent), typeof(MedicationEnrichmentFailedNotificationConsumer), "notifications-medication-enrichment-failed"),
        };
        builder.Services.AddFamilyHubMessaging(builder.Configuration, kafkaConsumers,
            typeof(DomainEventPublisher).Assembly,
            typeof(MedicalModule).Assembly,
            typeof(BirthdayModule).Assembly);

        return builder;
    }
}
