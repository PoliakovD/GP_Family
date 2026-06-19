using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>Many-to-many User &lt;-&gt; Family + роль. UNIQUE(FamilyId, UserId) — членство в нескольких семьях уже заложено.</summary>
public class FamilyMember : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public FamilyRole Role { get; set; }

    /// <summary>PendingApproval не даёт доступа ни к чему, даже к семейным ресурсам.</summary>
    public MemberStatus Status { get; set; }

    public DateTime JoinedAt { get; set; }
}
