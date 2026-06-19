namespace FamilyHub.Domain.Entities;

/// <summary>
/// УРОВЕНЬ 1 шаринга анализов: владелец открыл ВСЕ свои анализы конкретной семье,
/// одним действием. UNIQUE(OwnerUserId, FamilyId).
/// </summary>
public class FamilyMedicalShare
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public Guid FamilyId { get; set; }

    public DateTime SharedAt { get; set; }
}
