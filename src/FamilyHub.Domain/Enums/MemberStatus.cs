namespace FamilyHub.Domain.Enums;

/// <summary>
/// Статус членства в семье. PendingApproval не даёт доступа ни к чему,
/// даже к семейным ресурсам — все проверки доступа требуют Active.
/// </summary>
public enum MemberStatus
{
    PendingApproval = 0,
    Active = 1,
}
