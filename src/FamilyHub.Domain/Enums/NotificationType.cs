namespace FamilyHub.Domain.Enums;

/// <summary>Виды оповещений, формируемых фоновой джобой (этап 3 п.10 брифа).</summary>
public enum NotificationType
{
    /// <summary>Срок годности лекарства приближается (в пределах окна предупреждения).</summary>
    MedicationExpiringSoon = 0,

    /// <summary>Срок годности лекарства уже истёк.</summary>
    MedicationExpired = 1,

    /// <summary>День рождения наступает в пределах окна предупреждения.</summary>
    BirthdayUpcoming = 2,
}
