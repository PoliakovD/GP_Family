using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Members;

/// <summary>
/// Выгон и самовыход (раздел 8 брифа). Выгнать может только Admin; выйти — любой участник
/// без требования роли. В обоих случаях последнего активного админа убрать нельзя.
/// Отзыв FamilyMedicalShare ушедшего выполняет Medical-модуль по событию UserLeftFamilyEvent
/// (этап 1 плана, шина — ADR-0006): событие публикуется в одной транзакции с удалением членства.
/// </summary>
public class MembershipService(AppDbContext db, IFamilyAccessService access, IDomainEventPublisher publisher, ILogger<MembershipService> logger)
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
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var member = await db.FamilyMembers
            .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.UserId == targetUserId, ct);
        if (member is null)
        {
            await tx.RollbackAsync(ct);
            return CoreOutcome.NotFound;
        }

        if (member.Role == FamilyRole.Admin && member.Status == MemberStatus.Active)
        {
            var activeAdminCount = await CountActiveAdminsLockedAsync(familyId, ct);
            if (activeAdminCount <= 1)
            {
                await tx.RollbackAsync(ct);
                return CoreOutcome.LastAdmin;
            }
        }

        db.FamilyMembers.Remove(member);
        // Вышел/выгнан → его анализы перестают быть видны этой семье (шары отзовёт Medical-потребитель
        // события; сами записи и сканы остаются у владельца), а админов оповестит Notifications.
        await publisher.PublishAsync(new UserLeftFamilyEvent(familyId, targetUserId), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CoreOutcome.Removed;
    }

    /// <summary>
    /// Считает активных админов семьи, заблокировав их строки на время транзакции (аудит,
    /// находка Critical #4): без FOR UPDATE обычный CountAsync не защищает от гонки — два
    /// одновременных выхода/выгона двух РАЗНЫХ последних админов могли оба прочитать
    /// adminCount == 2 до того, как любой из них закоммитил своё удаление, и оба пройти проверку.
    /// В продукте нет промоушена участника в Admin постфактум (см. FamilyService) — семья
    /// осталась бы без единого админа безвозвратно. FOR UPDATE сериализует: второй запрос ждёт
    /// коммита первого и пересчитывает уже по факту его удаления.
    ///
    /// PostgreSQL (прод, см. Program.cs — UseNpgsql безусловно) — единственная реальная цель
    /// деплоя; SQLite (только юнит-тесты, см. SqliteTestBase) не понимает синтаксис FOR UPDATE
    /// вовсе, но и не нуждается в нём для тестовой корректности — SQLite по умолчанию
    /// сериализует ПИСАТЕЛЕЙ на уровне всего файла БД (BEGIN IMMEDIATE/EXCLUSIVE), это более
    /// грубая, но для юнит-тестов достаточная гарантия.
    /// </summary>
    private async Task<int> CountActiveAdminsLockedAsync(Guid familyId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return (await db.FamilyMembers
                .FromSqlInterpolated($"""
                    SELECT * FROM identity."FamilyMembers"
                    WHERE "FamilyId" = {familyId} AND "Role" = {(int)FamilyRole.Admin} AND "Status" = {(int)MemberStatus.Active}
                    FOR UPDATE
                    """)
                .ToListAsync(ct)).Count;
        }

        return await db.FamilyMembers.CountAsync(
            m => m.FamilyId == familyId && m.Role == FamilyRole.Admin && m.Status == MemberStatus.Active, ct);
    }
}
