using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>Семья — владелец семейных ресурсов (аптечка, ДР, будущие чат/события).</summary>
public class Family
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Закладка под монетизацию.</summary>
    public PlanType PlanType { get; set; } = PlanType.Free;

    /// <summary>Закладка под монетизацию.</summary>
    public DateTime? PlanExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<FamilyMember> Members { get; set; } = [];
}
