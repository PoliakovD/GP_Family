namespace FamilyHub.Domain.Entities;

/// <summary>День рождения члена семьи — семейный ресурс.</summary>
public class Birthday : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    public string PersonName { get; set; } = string.Empty;

    public DateOnly Date { get; set; }
}
