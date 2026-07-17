namespace FamilyHub.Domain.Entities;

/// <summary>Аптечка — контейнер медикаментов, семейный ресурс. У семьи может быть несколько аптечек.</summary>
public class Medkit : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<Medication> Medications { get; set; } = [];
}
