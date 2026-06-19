using FamilyHub.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace FamilyHub.Infrastructure.Authorization;

public class FamilyRoleRequirement(FamilyRole minRole) : IAuthorizationRequirement
{
    public FamilyRole MinRole { get; } = minRole;
}
