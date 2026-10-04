using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Снимок строки справочника для журнала/отката (ADR-0018). SpecimenKbId — только для
/// показателей. Verification* — внутренний маркер (см. KbVerificationStatus), попадает только в журнал админки.</summary>
public record KbRecordSnapshot(
    Guid Id, string NormalizedName, Guid? SpecimenKbId, string DisplayName, string PayloadJson, string Source,
    string[] Aliases, string[] LockedFields, int PayloadVersion, KbVerificationStatus VerificationStatus,
    DateTime? VerifiedAt, string? VerifiedPayloadHash, DateTime CreatedAt);

/// <summary>Снимок строки кэша поиска (подмножество, которое меняет админ: сниппеты и override'ы).</summary>
/// <summary>DisplayName/Units/SearchGroupKey — с 2026-10-04; в старых записях журнала null (откат строки, удалённой
/// до этого, восстанавливает ключ группы из биоматериала).</summary>
public record SearchCacheSnapshot(
    Guid Id, string NormalizedName, Guid? SpecimenKbId, string Provider, DateTime LastUpdatedAt,
    DateTime CanBeUpdatedAfter, string? SnippetsJson, string? OverridesJson,
    string? DisplayName = null, string? Units = null, string? SearchGroupKey = null);

public record KbChangeLogItemDto(
    Guid Id, DateTime At, string Actor, KbChangeTarget Target, Guid TargetId, string TargetLabel, string Action,
    string? Note, Guid? RevertedLogId, bool CanRevert);

public record KbChangeLogDetailDto(
    Guid Id, DateTime At, string Actor, KbChangeTarget Target, Guid TargetId, string TargetLabel, string Action,
    string? Note, Guid? RevertedLogId, string? BeforeJson, string? AfterJson);

/// <summary>
/// Журнал изменений записей справочников и кэша поиска (ADR-0018). Запись — raw SQL, не EF: журнал
/// пишется изнутри писателей kb/процессоров с общим AppDbContext, и SaveChangesAsync там сохранил бы
/// чужие, ещё не готовые изменения (например, статус задачи посреди транзакции).
/// </summary>
public class KbChangeLogService(AppDbContext db, ILogger<KbChangeLogService> logger)
{
    public const string ActorAdmin = "admin";
    public const string ActorSystem = "system";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string? ToJson(KbRecordSnapshot? snapshot) =>
        snapshot is null ? null : JsonSerializer.Serialize(snapshot, JsonOptions);

    public static string? ToJson(SearchCacheSnapshot? snapshot) =>
        snapshot is null ? null : JsonSerializer.Serialize(snapshot, JsonOptions);

    public async Task RecordAsync(
        KbChangeTarget target, Guid targetId, string targetLabel, string action,
        string? beforeJson, string? afterJson, string actor = ActorAdmin, string? note = null,
        Guid? revertedLogId = null, CancellationToken ct = default)
    {
        var label = targetLabel.Length > 400 ? targetLabel[..400] : targetLabel;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO kb.change_log
                ("Id", "At", "Actor", "Target", "TargetId", "TargetLabel", "Action", "BeforeJson", "AfterJson", "Note", "RevertedLogId")
            VALUES
                ({Guid.NewGuid()}, {DateTime.UtcNow}, {actor}, {(int)target}, {targetId}, {label}, {action},
                 {beforeJson}, {afterJson}, {note}, {revertedLogId})
            """, ct);
        logger.LogDebug("Журнал kb: {Action} {Target} {TargetId} ({Actor})", action, target, targetId, actor);
    }

    public async Task<(List<KbChangeLogItemDto> Items, int Total)> ListAsync(
        KbChangeTarget? target, Guid? targetId, int skip, int take, CancellationToken ct = default)
    {
        var query = db.KbChangeLogs.AsNoTracking().AsQueryable();
        if (target is not null) query = query.Where(l => l.Target == target);
        if (targetId is not null) query = query.Where(l => l.TargetId == targetId);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(l => l.At).Skip(skip).Take(take).ToListAsync(ct);

        // Отменённая запись откатывается один раз: если уже есть откат, ссылающийся на неё — повторно нельзя.
        var ids = rows.Select(r => r.Id).ToList();
        var revertedIds = await db.KbChangeLogs.AsNoTracking()
            .Where(l => l.RevertedLogId != null && ids.Contains(l.RevertedLogId.Value))
            .Select(l => l.RevertedLogId!.Value).ToListAsync(ct);
        var reverted = revertedIds.ToHashSet();

        var items = rows.Select(l => new KbChangeLogItemDto(
            l.Id, l.At, l.Actor, l.Target, l.TargetId, l.TargetLabel, l.Action, l.Note, l.RevertedLogId,
            CanRevert: l.Action != "revert" && !reverted.Contains(l.Id)
                && (l.BeforeJson is not null || l.AfterJson is not null))).ToList();
        return (items, total);
    }

    public async Task<KbChangeLog?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.KbChangeLogs.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);

    public async Task<KbChangeLogDetailDto?> GetDetailAsync(Guid id, CancellationToken ct = default)
    {
        var l = await GetAsync(id, ct);
        return l is null ? null : new KbChangeLogDetailDto(
            l.Id, l.At, l.Actor, l.Target, l.TargetId, l.TargetLabel, l.Action, l.Note, l.RevertedLogId,
            l.BeforeJson, l.AfterJson);
    }

    public async Task<bool> WasRevertedAsync(Guid logId, CancellationToken ct = default) =>
        await db.KbChangeLogs.AsNoTracking().AnyAsync(l => l.RevertedLogId == logId, ct);
}
