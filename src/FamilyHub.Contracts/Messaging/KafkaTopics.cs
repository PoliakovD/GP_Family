using FamilyHub.Contracts.Events;

namespace FamilyHub.Contracts.Messaging;

/// <summary>
/// Топик на событие, kebab-case, явные константы — не выводятся рефлексией из имени типа,
/// чтобы переименование C#-класса не переименовывало тихо боевой топик (ADR-0006). Покрытие
/// всех FamilyHub.Contracts.Events проверяется KafkaTopicsTests.
///
/// Живёт в Contracts (не в Infrastructure, как раньше), потому что после выноса бота
/// (FamilyHub.TelegramBot) имена топиков стали межпроцессным контрактом — бот не ссылается на
/// Infrastructure (там EF/Npgsql/Minio/Hangfire, не нужные процессу без БД), но обязан знать
/// точное имя topic'а, который он потребляет.
/// </summary>
public static class KafkaTopics
{
    public const string MedicalRecordShared = "medical-record-shared";
    public const string UserLeftFamily = "user-left-family";
    public const string MemberApproved = "member-approved";
    public const string MedicationExpiring = "medication-expiring";
    public const string BirthdayApproaching = "birthday-approaching";
    public const string MedicationEnriched = "medication-enriched";
    public const string MedicalDocumentExtracted = "medical-document-extracted";

    /// <summary>
    /// Исходящие сообщения для Telegram-бота (TelegramOutboundPublisher → TelegramOutboundConsumer).
    /// Единственный топик, который потребляет FamilyHub.TelegramBot, а не FamilyHub.Api.
    /// </summary>
    public const string TelegramOutbound = "telegram-outbound";

    public static IReadOnlyDictionary<Type, string> ByEventType { get; } = new Dictionary<Type, string>
    {
        [typeof(MedicalRecordSharedEvent)] = MedicalRecordShared,
        [typeof(UserLeftFamilyEvent)] = UserLeftFamily,
        [typeof(MemberApprovedEvent)] = MemberApproved,
        [typeof(MedicationExpiringEvent)] = MedicationExpiring,
        [typeof(BirthdayApproachingEvent)] = BirthdayApproaching,
        [typeof(MedicationEnrichedEvent)] = MedicationEnriched,
        [typeof(MedicalDocumentExtractedEvent)] = MedicalDocumentExtracted,
        [typeof(TelegramMessageRequestedEvent)] = TelegramOutbound,
    };
}
