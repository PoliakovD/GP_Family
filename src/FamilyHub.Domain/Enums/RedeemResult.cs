namespace FamilyHub.Domain.Enums;

/// <summary>Результат попытки принять инвайт (см. InviteService.RedeemInviteAsync).</summary>
public enum RedeemResult
{
    NotFound,
    Revoked,
    Expired,
    Exhausted,
    NotForYou,
    AlreadyMember,
    Joined,          // персональный инвайт — вступил сразу (Active)
    PendingApproval, // ссылка-инвайт — ждёт одобрения админа
}
