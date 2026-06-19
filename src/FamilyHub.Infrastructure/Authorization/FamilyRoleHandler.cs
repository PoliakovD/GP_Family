using FamilyHub.Domain;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Authorization;

/// <summary>
/// Resource-based authorization handler: проверяет членство в семье ресурса + минимальную
/// роль. PendingApproval не даёт доступа ни к чему, даже к семейным ресурсам — допускается
/// только Status == Active. Инвариант 3 из брифа: семейные ресурсы — через роль в той
/// семье, которой принадлежит ресурс.
/// </summary>
public class FamilyRoleHandler(AppDbContext db)
    : AuthorizationHandler<FamilyRoleRequirement, IFamilyOwned>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FamilyRoleRequirement requirement,
        IFamilyOwned resource)
    {
        var userId = context.User.GetUserId();
        if (userId is null)
            return;

        var membership = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(m =>
            m.FamilyId == resource.FamilyId && m.UserId == userId.Value);

        if (membership is { Status: MemberStatus.Active } && membership.Role >= requirement.MinRole)
            context.Succeed(requirement);
    }
}
