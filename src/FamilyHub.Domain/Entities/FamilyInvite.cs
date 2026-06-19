using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Приглашение в семью — одна таблица покрывает все сценарии: одноразовая ссылка,
/// многоразовая, персональная, с истечением.
/// Персональный (TargetUserId задан) → вступление сразу Active.
/// Ссылка (TargetUserId = null) → вступление PendingApproval, пока админ не подтвердит.
/// </summary>
public class FamilyInvite : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    /// <summary>Должен быть Admin семьи на момент создания.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>Случайный токен (UNIQUE, индекс).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Если задан — персональный инвайт, примет только этот пользователь.</summary>
    public Guid? TargetUserId { get; set; }

    public FamilyRole AssignedRole { get; set; } = FamilyRole.Member;

    /// <summary>Лимит использований (1 = одноразовая).</summary>
    public int MaxUses { get; set; } = 1;

    public int UsedCount { get; set; }

    /// <summary>Срок жизни (null = бессрочно, не рекомендуется).</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Админ может отозвать вручную.</summary>
    public bool IsRevoked { get; set; }

    public DateTime CreatedAt { get; set; }
}
