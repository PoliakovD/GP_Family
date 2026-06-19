namespace FamilyHub.Domain.Entities;

/// <summary>
/// УРОВЕНЬ 2 шаринга анализов: точечное скрытие конкретной записи от конкретной семьи,
/// из числа тех, кому уже открыт доступ. UNIQUE(MedicalRecordId, FamilyId).
/// </summary>
public class MedicalRecordHidden
{
    public Guid Id { get; set; }

    public Guid MedicalRecordId { get; set; }
    public MedicalRecord MedicalRecord { get; set; } = null!;

    public Guid FamilyId { get; set; }

    public DateTime HiddenAt { get; set; }
}
