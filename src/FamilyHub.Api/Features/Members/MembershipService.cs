using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Outbox;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Members;

/// <summary>
/// Выгон и самовыход (раздел 8 брифа). Выгнать может только Admin; выйти — любой участник
/// без требования роли. В обоих случаях последнего активного админа убрать нельзя.
/// Отзыв FamilyMedicalShare ушедшего выполняет Medical-модуль по событию UserLeftFamilyEvent
/// (этап 1 плана): событие пишется в outbox в одной транзакции с удалением членства.
/// </summary>
public class MembershipService(AppDbContext db, IFamilyAccessService access, IOutboxWriter outbox, ILogger<MembershipService> logger)
{
    public async Task<RemoveMemberResult> RemoveMemberAsync(Guid familyId, Guid targetUserId, Guid requestingUserId, CancellationToken ct = default)
    {
        if (!await access.HasRoleAsync(requestingUserId, familyId, FamilyRole.Admin, ct))
        {
            logger.LogWarning(
                "Выгон участника отклонён: {UserId} не админ семьи {FamilyId}", requestingUserId, familyId);
            return RemoveMemberResult.Forbidden;
        }

        var outcome = await RemoveMembershipCoreAsync(familyId, targetUserId, ct);
        LogOutcome("Выгон", familyId, targetUserId, requestingUserId, outcome);
        return outcome switch
        {
            CoreOutcome.NotFound => RemoveMemberResult.NotFound,
            CoreOutcome.LastAdmin => RemoveMemberResult.LastAdmin,
            _ => RemoveMemberResult.Removed,
        };
    }

    public async Task<LeaveFamilyResult> LeaveFamilyAsync(Guid familyId, Guid userId, CancellationToken ct = default)
    {
        var outcome = await RemoveMembershipCoreAsync(familyId, userId, ct);
        LogOutcome("Выход", familyId, userId, userId, outcome);
        return outcome switch
        {
            CoreOutcome.NotFound => LeaveFamilyResult.NotFound,
            CoreOutcome.LastAdmin => LeaveFamilyResult.LastAdmin,
            _ => LeaveFamilyResult.Left,
        };
    }

    private void LogOutcome(string action, Guid familyId, Guid targetUserId, Guid requestingUserId, CoreOutcome outcome)
    {
        switch (outcome)
        {
            case CoreOutcome.Removed:
                logger.LogInformation(
                    "{Action}: пользователь {TargetUserId} покинул семью {FamilyId} (инициатор {RequestingUserId})",
                    action, targetUserId, familyId, requestingUserId);
                break;
            case CoreOutcome.LastAdmin:
                logger.LogWarning(
                    "{Action} отклонён: {TargetUserId} — последний активный админ семьи {FamilyId}",
                    action, targetUserId, familyId);
                break;
            case CoreOutcome.NotFound:
                logger.LogWarning(
                    "{Action} отклонён: {TargetUserId} не состоит в семье {FamilyId}", action, targetUserId, familyId);
                break;
        }
    }

    private enum CoreOutcome { Removed, NotFound, LastAdmin }

    private async Task<CoreOutcome> RemoveMembershipCoreAsync(Guid familyId, Guid targetUserId, CancellationToken ct)
    {
        var member = await db.FamilyMembers
            .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.UserId == targetUserId, ct);
        if (member is null) return CoreOutcome.NotFound;

        if (member.Role == FamilyRole.Admin && member.Status == MemberStatus.Active)
        {
            var adminCount = await db.FamilyMembers.CountAsync(m =>
                m.FamilyId == familyId && m.Role == FamilyRole.Admin && m.Status == MemberStatus.Active, ct);
            if (adminCount <= 1) return CoreOutcome.LastAdmin;
        }

        db.FamilyMembers.Remove(member);
        // Вышел/выгнан → его анализы перестают быть видны этой семье (шары отзовёт Medical-хендлер
        // события; сами записи и сканы остаются у владельца), а админов оповестит Notifications.
        outbox.Enqueue(new UserLeftFamilyEvent(familyId, targetUserId));
        await db.SaveChangesAsync(ct);
        return CoreOutcome.Removed;
    }
}
