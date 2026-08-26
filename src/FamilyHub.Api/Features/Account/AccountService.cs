using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Storage;
using FamilyHub.Modules.Medical.Attachments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Api.Features.Account;

public record LastAdminFamily(Guid FamilyId, string Name);

public record DeleteAccountOutcome(bool Deleted, List<LastAdminFamily> BlockingFamilies);

/// <summary>
/// Права субъекта ПДн (задача 2.3): удаление аккаунта «в один клик» и экспорт данных.
/// Удаление каскадное: медзаписи (без FK на User — чистятся явно) + файлы в хранилище,
/// шары, коды, инвайты, членства; сам User — последним. UserConsents и строки аудита
/// (только UUID) сохраняются как юридическое доказательство — см. backup-and-retention-policy.
/// </summary>
public class AccountService(
    AppDbContext db,
    IFileStorage storage,
    IMedicalAuditWriter audit,
    ILogger<AccountService> logger)
{
    public async Task<DeleteAccountOutcome> DeleteAccountAsync(Guid userId, CancellationToken ct = default)
    {
        var memberships = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.FamilyId, m.Role, m.Status, FamilyName = m.Family.Name })
            .ToListAsync(ct);

        // Guard: «последний админ, но не последний член» — семья осталась бы без управления.
        // Пользователь должен сначала передать админство или удалить семью целиком.
        var blocking = new List<LastAdminFamily>();
        var soleMemberFamilyIds = new List<Guid>();
        foreach (var membership in memberships)
        {
            var otherMembers = await db.FamilyMembers.AsNoTracking()
                .CountAsync(m => m.FamilyId == membership.FamilyId && m.UserId != userId, ct);
            if (otherMembers == 0)
            {
                soleMemberFamilyIds.Add(membership.FamilyId);
                continue;
            }

            if (membership is { Role: FamilyRole.Admin, Status: MemberStatus.Active })
            {
                var otherActiveAdmins = await db.FamilyMembers.AsNoTracking().CountAsync(
                    m => m.FamilyId == membership.FamilyId && m.UserId != userId
                        && m.Role == FamilyRole.Admin && m.Status == MemberStatus.Active, ct);
                if (otherActiveAdmins == 0)
                    blocking.Add(new LastAdminFamily(membership.FamilyId, membership.FamilyName));
            }
        }

        if (blocking.Count > 0) return new DeleteAccountOutcome(false, blocking);

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);

        // Ключи хранилища собираем до удаления строк; сами объекты удаляем после коммита.
        var recordIds = await db.MedicalRecords.Where(r => r.OwnerUserId == userId).Select(r => r.Id).ToListAsync(ct);
        var storageKeys = await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && recordIds.Contains(a.OwnerId))
            .Select(a => a.StorageKey)
            .ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Семьи, где пользователь один: удаляются целиком (каскад БД + явная чистка
        // shares/hidden — тот же порядок, что в FamilyService.DeleteFamilyAsync).
        foreach (var familyId in soleMemberFamilyIds)
        {
            await db.FamilyMedicalShares.Where(s => s.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await db.MedicalRecordHiddens.Where(h => h.FamilyId == familyId).ExecuteDeleteAsync(ct);
            await db.Families.Where(f => f.Id == familyId).ExecuteDeleteAsync(ct);
        }

        // Персональные медданные (FK на User нет — явная чистка).
        await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && recordIds.Contains(a.OwnerId))
            .ExecuteDeleteAsync(ct);
        await db.MedicalRecords.Where(r => r.OwnerUserId == userId).ExecuteDeleteAsync(ct); // hidden — каскадом от записей
        await db.FamilyMedicalShares.Where(s => s.OwnerUserId == userId).ExecuteDeleteAsync(ct);

        // Идентификационные хвосты.
        await db.EmailVerificationCodes
            .Where(c => c.UserId == userId || (user.Email != null && c.Email == user.Email))
            .ExecuteDeleteAsync(ct);
        await db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserNotificationPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.FamilyInviteRedemptions.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.FamilyInvites.Where(i => i.TargetUserId == userId || i.CreatedByUserId == userId)
            .ExecuteDeleteAsync(ct);

        // User: членства и оповещения каскадом (FK), UserConsents намеренно остаются.
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

        // Аудит erasure — в той же транзакции; строка без FK переживает удаление пользователя.
        await audit.WriteAsync(userId, MedicalAccessAction.Erasure, ownerUserId: userId, ct: ct);

        await tx.CommitAsync(ct);

        // Файлы — после коммита БД: сбой удаления объекта не откатывает право на забвение
        // в БД, а осиротевший шифрованный блоб нечитаем без строки FileAttachment и ключа.
        foreach (var key in storageKeys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Erasure: не удалось удалить блоб {StorageKey} — требуется ручная зачистка", key);
            }
        }

        logger.LogInformation(
            "Erasure: аккаунт {UserId} удалён ({Records} медзаписей, {Files} файлов, {Families} семей целиком)",
            userId, recordIds.Count, storageKeys.Count, soleMemberFamilyIds.Count);
        return new DeleteAccountOutcome(true, []);
    }

    /// <summary>Экспорт всех данных пользователя (задача 2.3): zip с JSON + расшифрованными вложениями.</summary>
    public async Task WriteExportZipAsync(Guid userId, Stream destination, AttachmentService attachments, CancellationToken ct = default)
    {
        await audit.WriteAsync(userId, MedicalAccessAction.Export, ownerUserId: userId, ct: ct);

        // ZipArchive финализирует записи синхронным Write — Kestrel запрещает sync-IO в ответ.
        // Объёмы персонального экспорта невелики: собираем архив в памяти, затем отдаём async.
        using var buffer = new MemoryStream();
        await BuildZipAsync(userId, buffer, attachments, ct);
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, ct);
    }

    private async Task BuildZipAsync(Guid userId, Stream destination, AttachmentService attachments, CancellationToken ct)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // Экспорт читает человек: кириллица как текст, а не \uXXXX-эскейпы.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        await AddJsonAsync(zip, "profile.json", new
        {
            user.Id,
            user.LastName,
            user.FirstName,
            user.MiddleName,
            user.BirthDate,
            user.Gender,
            user.Username,
            user.TgUsername,
            user.Email,
            hasTelegram = user.TelegramId is not null,
            user.CreatedAt,
        }, jsonOptions, ct);

        var consents = await db.Set<UserConsent>().AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Kind, c.Version, c.AcceptedAt })
            .ToListAsync(ct);
        await AddJsonAsync(zip, "consents.json", consents, jsonOptions, ct);

        var families = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.FamilyId, m.Family.Name, m.Role, m.Status, m.JoinedAt })
            .ToListAsync(ct);
        await AddJsonAsync(zip, "families.json", families, jsonOptions, ct);

        // Поля записей расшифровываются EF-конвертером прозрачно — субъект получает читаемые данные.
        var records = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.OwnerUserId == userId)
            .ToListAsync(ct);
        await AddJsonAsync(zip, "medical-records.json",
            records.Select(r => new
            {
                r.Id, r.Title, r.RecordDate, r.Doctor, r.Description,
                r.FamilyDependentId, r.TargetUserId, r.CreatedAt,
            }),
            jsonOptions, ct);

        var recordIds = records.Select(r => r.Id).ToList();
        var attachmentRows = await db.FileAttachments.AsNoTracking()
            .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && recordIds.Contains(a.OwnerId))
            .ToListAsync(ct);
        foreach (var row in attachmentRows)
        {
            var download = await attachments.GetDownloadAsync(row.Id, ct);
            if (download is null) continue;

            // Defense in depth: имя уже санитизируется на загрузке (AttachmentService), но
            // сверяем ещё раз здесь — единственное место, где имя файла становится частью пути
            // внутри архива (Zip Slip, см. аудит module-review-2026-08-02, находка 1). Покрывает
            // и гипотетические строки, записанные в обход AttachmentService.
            var safeFileName = FileNameSanitizer.Sanitize(download.Value.FileName);
            var entry = zip.CreateEntry($"attachments/{row.OwnerId}/{safeFileName}");
            await using var entryStream = entry.Open();
            await using var content = download.Value.Content;
            await content.CopyToAsync(entryStream, ct);
        }
    }

    private static async Task AddJsonAsync(
        ZipArchive zip, string name, object payload, JsonSerializerOptions options, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, options)), ct);
    }
}
