namespace FamilyHub.Domain.Entities;

/// <summary>Лог принятий инвайта. UNIQUE(FamilyInviteId, UserId) — один юзер не редимит один инвайт повторно.</summary>
public class FamilyInviteRedemption
{
    public Guid Id { get; set; }

    public Guid FamilyInviteId { get; set; }
    public FamilyInvite FamilyInvite { get; set; } = null!;

    public Guid UserId { get; set; }

    public DateTime RedeemedAt { get; set; }
}
