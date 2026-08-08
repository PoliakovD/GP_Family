using FamilyHub.Contracts.Events;

namespace FamilyHub.Infrastructure.Messaging.Kafka;

/// <summary>
/// Топик на событие, kebab-case, явные константы — не выводятся рефлексией из имени типа,
/// чтобы переименование C#-класса не переименовывало тихо боевой топик (ADR-0006). Покрытие
/// всех FamilyHub.Contracts.Events проверяется KafkaTopicsTests.
/// </summary>
public static class KafkaTopics
{
    public const string MedicalRecordShared = "medical-record-shared";
    public const string UserLeftFamily = "user-left-family";
    public const string MemberApproved = "member-approved";
    public const string MedicationExpiring = "medication-expiring";
    public const string BirthdayApproaching = "birthday-approaching";
    public const string MedicationEnriched = "medication-enriched";

    public static IReadOnlyDictionary<Type, string> ByEventType { get; } = new Dictionary<Type, string>
    {
        [typeof(MedicalRecordSharedEvent)] = MedicalRecordShared,
        [typeof(UserLeftFamilyEvent)] = UserLeftFamily,
        [typeof(MemberApprovedEvent)] = MemberApproved,
        [typeof(MedicationExpiringEvent)] = MedicationExpiring,
        [typeof(BirthdayApproachingEvent)] = BirthdayApproaching,
        [typeof(MedicationEnrichedEvent)] = MedicationEnriched,
    };
}
