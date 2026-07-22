namespace FamilyHub.Domain.Entities;

/// <summary>
/// Мед-анализ — персональный ресурс. Принадлежит пользователю (OwnerUserId), НЕ семье.
/// По умолчанию приватен. НЕ реализует IFamilyOwned — видимость определяется
/// FamilyMedicalShare + MedicalRecordHidden, а не ролью в семье.
/// ПДн-поля шифруются at-rest (этап 2): в БД запись обезличена до OwnerUserId (UUID).
/// </summary>
public class MedicalRecord
{
    public Guid Id { get; set; }

    /// <summary>Владелец записи. Только он управляет шарингом и скрытием.</summary>
    public Guid OwnerUserId { get; set; }

    [Encrypted]
    public string PersonName { get; set; } = string.Empty;

    public DateOnly RecordDate { get; set; }

    [Encrypted]
    public string? Doctor { get; set; }

    [Encrypted]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
