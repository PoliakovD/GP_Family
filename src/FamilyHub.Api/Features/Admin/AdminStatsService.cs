using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Auth.Jwt;
using FamilyHub.Infrastructure.Consents;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Статистика для четырёх вкладок админ-панели (ADR-0009): «Обзор», «Хранилище», «Система»,
/// «Ключи»/«Безопасность». Только чтение — ничего не изменяет. Хранилище кэшируется вызывающей
/// стороной (AdminEndpoints, IMemoryCache) — полный листинг бакета дорог, вызывать редко.
/// </summary>
public class AdminStatsService(
    AppDbContext db,
    IFileStorage storage,
    IEncryptionKeyRing encryptionKeyRing,
    IOptions<JwtOptions> jwtOptions,
    IOptions<ConsentOptions> consentOptions,
    HealthCheckService healthChecks)
{
    public async Task<AdminOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var last7 = now.AddDays(-7);
        var last30 = now.AddDays(-30);

        var totalUsers = await db.Users.CountAsync(ct);
        var telegramOnly = await db.Users.CountAsync(u => u.TelegramId != null && u.Email == null, ct);
        var pwaOnly = await db.Users.CountAsync(u => u.TelegramId == null && u.Email != null, ct);
        var both = await db.Users.CountAsync(u => u.TelegramId != null && u.Email != null, ct);
        var new7 = await db.Users.CountAsync(u => u.CreatedAt >= last7, ct);
        var new30 = await db.Users.CountAsync(u => u.CreatedAt >= last30, ct);
        var lockedOut = await db.Users.CountAsync(u => u.LockedUntil != null && u.LockedUntil > now, ct);

        var totalFamilies = await db.Families.CountAsync(ct);
        var activeMemberCountsByFamily = await db.FamilyMembers
            .Where(m => m.Status == MemberStatus.Active)
            .GroupBy(m => m.FamilyId)
            .Select(g => g.Count())
            .ToListAsync(ct);

        var medicalRecords = await db.MedicalRecords.CountAsync(ct);
        var medications = await db.Medications.CountAsync(ct);
        var expiredMedications = await db.Medications
            .CountAsync(m => m.ExpiryDate != null && m.ExpiryDate < DateOnly.FromDateTime(now), ct);
        var birthdays = await db.Birthdays.CountAsync(ct);
        var attachments = await db.FileAttachments.CountAsync(ct);
        var dependents = await db.FamilyDependents.CountAsync(ct);

        return new AdminOverviewDto(
            new UsersOverviewDto(totalUsers, telegramOnly, pwaOnly, both, new7, new30, lockedOut),
            new FamiliesOverviewDto(
                totalFamilies,
                activeMemberCountsByFamily.Count,
                activeMemberCountsByFamily.Count == 0 ? 0 : activeMemberCountsByFamily.Average()),
            new DomainCountsDto(medicalRecords, medications, expiredMedications, birthdays, attachments, dependents));
    }

    public async Task<AdminStorageStatsDto> GetStorageStatsAsync(CancellationToken ct = default)
    {
        var attachments = await db.FileAttachments.AsNoTracking()
            .Select(a => new { a.StorageKey, a.SizeBytes })
            .ToListAsync(ct);
        var attachmentsByKey = attachments
            .GroupBy(a => a.StorageKey)
            .ToDictionary(g => g.Key, g => g.First().SizeBytes);

        long bucketSize = 0;
        var bucketKeys = new HashSet<string>();
        await foreach (var obj in storage.ListAsync(ct))
        {
            bucketSize += obj.SizeBytes;
            bucketKeys.Add(obj.Key);
        }

        // Осиротевшие блобы — объект в бакете, на который не ссылается ни одна строка БД (место
        // утекло: неудалённый файл после сбоя/бага). Битые вложения — обратная ситуация: строка
        // есть, а объекта в бакете нет (скачивание такого вложения упадёт).
        var orphanedBlobs = bucketKeys.Count(key => !attachmentsByKey.ContainsKey(key));
        var brokenAttachments = attachmentsByKey.Keys.Count(key => !bucketKeys.Contains(key));

        return new AdminStorageStatsDto(
            bucketSize, bucketKeys.Count,
            attachmentsByKey.Values.Sum(), attachmentsByKey.Count,
            new StorageReconciliationDto(orphanedBlobs, brokenAttachments),
            DateTime.UtcNow);
    }

    public async Task<AdminSystemStatsDto> GetSystemStatsAsync(CancellationToken ct = default)
    {
        // MassTransit EF outbox (ADR-0006) — не типизированный DbSet в AppDbContext (таблицы
        // созданы AddOutboxStateEntity, не публичным DbSet), поэтому сырой SQL по именам таблиц,
        // подтверждённым SchemaSeparationTests. Delivered IS NULL — батч ещё не полностью доставлен.
        var backlog = await db.Database.SqlQueryRaw<OutboxBacklogRow>(
            """SELECT COUNT(*) AS "UndeliveredBatches", MIN("Created") AS "OldestUndeliveredAt" FROM "OutboxState" WHERE "Delivered" IS NULL""")
            .SingleAsync(ct);

        var monitoring = JobStorage.Current.GetMonitoringApi();
        var queues = monitoring.Queues()
            .Select(q => new HangfireQueueDto(q.Name, q.Length))
            .ToList();
        var failedTotal = monitoring.FailedCount();

        var report = await healthChecks.CheckHealthAsync(check => check.Tags.Contains("ready"), ct);
        bool IsHealthy(string name) =>
            report.Entries.TryGetValue(name, out var entry) && entry.Status == HealthStatus.Healthy;

        return new AdminSystemStatsDto(
            new OutboxBacklogDto(backlog.UndeliveredBatches, backlog.OldestUndeliveredAt),
            queues, failedTotal,
            IsHealthy("postgres"), IsHealthy("minio"), IsHealthy("kafka"));
    }

    public async Task<AdminSecurityStatsDto> GetSecurityStatsAsync(CancellationToken ct = default)
    {
        // Префикс "enc:{keyId}:" — открытый текст (см. ADR-0002/0009), split_part читает его без
        // расшифровки. UNION по одной репрезентативной [Encrypted]-колонке на сущность — этого
        // достаточно для распределения по ключу (все колонки одной строки пишутся одним
        // SaveChanges, тем же активным ключом). MedicalRecords.Doctor — nullable (v2, PersonName
        // убран, у записи больше нет НИ ОДНОЙ обязательной [Encrypted]-колонки) — записи, где
        // Doctor не заполнен, здесь не посчитаны; для диагностической панели ротации это
        // приемлемое приближение, не источник истины о полноте перешифровки.
        var fieldRows = await db.Database.SqlQueryRaw<KeyIdCountRow>(
            """
            SELECT "KeyId", SUM("Cnt")::int AS "Cnt" FROM (
                SELECT split_part("Doctor", ':', 2) AS "KeyId", COUNT(*) AS "Cnt" FROM medical."MedicalRecords" WHERE "Doctor" IS NOT NULL GROUP BY 1
                UNION ALL
                SELECT split_part("PersonName", ':', 2), COUNT(*) FROM identity."Birthdays" GROUP BY 1
                UNION ALL
                SELECT split_part("FirstName", ':', 2), COUNT(*) FROM identity."FamilyDependents" GROUP BY 1
                UNION ALL
                SELECT split_part("Endpoint", ':', 2), COUNT(*) FROM identity."PushSubscriptions" GROUP BY 1
                UNION ALL
                SELECT split_part("FileName", ':', 2), COUNT(*) FROM medical."FileAttachments" GROUP BY 1
            ) x
            GROUP BY "KeyId"
            """).ToListAsync(ct);

        var blobRows = await db.FileAttachments.AsNoTracking()
            .Where(a => a.IsEncrypted)
            .GroupBy(a => a.KeyId)
            .Select(g => new KeyIdCountDto(g.Key ?? "?", g.Count()))
            .ToListAsync(ct);

        var last30 = DateTime.UtcNow.AddDays(-30);
        var crossUserAccess = await db.Set<MedicalAccessAudit>()
            .CountAsync(a => a.OccurredAt >= last30 && a.OwnerUserId != null && a.OwnerUserId != a.ActorUserId, ct);

        var usersWithCurrentConsent = await db.Set<UserConsent>()
            .Where(c => c.Kind == ConsentKind.PdnConsent && c.Version == consentOptions.Value.CurrentVersion)
            .Select(c => c.UserId)
            .Distinct()
            .CountAsync(ct);
        var totalUsers = await db.Users.CountAsync(ct);

        var now = DateTime.UtcNow;
        var activeSessions = await db.UserSessions.CountAsync(s => s.RevokedAt == null && s.ExpiresAt > now, ct);

        var dpKeys = await db.DataProtectionKeys.AsNoTracking().ToListAsync(ct);

        return new AdminSecurityStatsDto(
            new EncryptionKeyDistributionDto(
                fieldRows.Select(r => new KeyIdCountDto(r.KeyId, r.Cnt)).ToList(),
                blobRows),
            crossUserAccess,
            Math.Max(0, totalUsers - usersWithCurrentConsent),
            activeSessions,
            dpKeys.Count,
            null); // DataProtectionKey.Xml несёт дату создания внутри XML, не отдельной колонкой — не парсим здесь.
    }

    public AdminKeyRingsDto GetKeyRings()
    {
        var jwt = jwtOptions.Value;
        return new AdminKeyRingsDto(
            new EncryptionKeyRingDto(encryptionKeyRing.ActiveKeyId, encryptionKeyRing.PreviousKeyIds),
            new JwtKeyRingDto(jwt.ActiveKeyId, jwt.PreviousSigningKeys.Select(k => k.Id).ToList()),
            new DownloadKeyRingDto(0));
    }

    private record OutboxBacklogRow(int UndeliveredBatches, DateTime? OldestUndeliveredAt);
    private record KeyIdCountRow(string KeyId, int Cnt);
}
