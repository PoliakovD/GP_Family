using System.Security.Cryptography;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Previews;
using FamilyHub.Infrastructure.Security;
using FamilyHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.DoctorReports;

/// <summary>
/// Отчёт для врача: сборка PDF из данных пациента, хранение снимка и выдача публичной ссылки.
/// Пациент — сам автор, подопечный или взрослый член семьи (DoctorReportSubjects). Выдать ссылку и удалить
/// может только автор (OwnerUserId); взрослый пациент видит отчёт о себе, открывает PDF и может отозвать
/// ссылку — данные его, и решать, кому они сейчас открыты, тоже ему. Чужой отчёт — NotFound. Публичный доступ — только
/// по токену: в БД лежит хеш, ссылка живёт ограниченное время и отзывается; неверный, истёкший и
/// отозванный токены неразличимы для вызывающего (один и тот же null).
/// </summary>
public class DoctorReportService(
    AppDbContext db,
    DoctorReportDataCollector collector,
    DoctorReportSubjects subjects,
    NotificationSendingService notifications,
    IGotenbergConverter gotenberg,
    IFileCipher fileCipher,
    IFileStorage storage,
    IEncryptionKeyRing keyRing,
    IMedicalAuditWriter audit,
    ILogger<DoctorReportService> logger)
{
    public const int MaxPeriodDays = 731;
    public const int MaxReportsPerUser = 30;
    public const int MaxCommentLength = 4000;
    public const int MaxRecipientLength = 200;
    public static readonly IReadOnlyList<int> AllowedShareDays = [7, 14, 30];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Владелец ----

    /// <summary>Мои отчёты и отчёты обо мне, составленные членами семьи.</summary>
    public async Task<List<DoctorReportDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.DoctorReports.AsNoTracking()
            .Where(r => r.OwnerUserId == userId || r.SubjectUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        var authors = await AuthorNamesAsync(rows.Where(r => r.OwnerUserId != userId).Select(r => r.OwnerUserId), ct);
        var now = DateTime.UtcNow;
        return rows.Select(r => ToDto(r, now, userId, authors)).ToList();
    }

    public Task<List<DoctorReportSubjectDto>> ListSubjectsAsync(Guid userId, CancellationToken ct = default) =>
        subjects.ListAsync(userId, ct);

    public async Task<(DoctorReportResult Result, ReportCounts? Counts, string? Error)> PreviewAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default,
        DoctorReportSubjectKind subjectKind = DoctorReportSubjectKind.Self, Guid? subjectId = null)
    {
        var error = ValidatePeriod(from, to);
        if (error is not null) return (DoctorReportResult.Invalid, null, error);
        var subject = await subjects.ResolveAsync(userId, subjectKind, subjectId, ct);
        if (subject is null) return (DoctorReportResult.NotFound, null, null);
        return (DoctorReportResult.Success, await collector.CountAsync(subject, from, to, ct), null);
    }

    public async Task<(DoctorReportResult Result, DoctorReportDto? Item, string? Error)> CreateAsync(
        Guid userId, CreateDoctorReportRequest request, CancellationToken ct = default)
    {
        var error = Validate(request);
        if (error is not null) return (DoctorReportResult.Invalid, null, error);

        var subject = await subjects.ResolveAsync(userId, request.SubjectKind, request.SubjectId, ct);
        if (subject is null) return (DoctorReportResult.NotFound, null, null);

        if (await db.DoctorReports.CountAsync(r => r.OwnerUserId == userId, ct) >= MaxReportsPerUser)
            return (DoctorReportResult.TooMany, null, $"Достигнут лимит — {MaxReportsPerUser} отчётов. Удалите ненужные.");

        var blocks = new ReportBlocks(
            request.IncludeLabs, request.IncludeAiSummaries, request.IncludeMedications,
            request.IncludeVisits, request.IncludeMeasurements, request.IncludeSymptomsNotes, request.IncludeVaccinations);
        var model = await collector.CollectAsync(subject, request.PeriodFrom, request.PeriodTo, blocks, request.PatientComment, ct);
        string? authorName = null;
        if (!subject.IsSelf)
        {
            authorName = (await AuthorNamesAsync([userId], ct)).GetValueOrDefault(userId);
            model = model with { Patient = model.Patient with { CompiledBy = authorName } };
        }
        if (!model.HasContent)
            return (DoctorReportResult.NoData, null, "За выбранный период нет данных для отчёта. Расширьте период или включите другие блоки.");

        var pdf = await gotenberg.ConvertHtmlToPdfAsync(
            DoctorReportHtmlRenderer.Render(model), DoctorReportHtmlRenderer.RenderFooter(model), ct);
        if (pdf is null || pdf.Length == 0)
        {
            logger.LogWarning("Отчёт для врача пользователя {UserId}: сервис PDF не вернул документ", userId);
            return (DoctorReportResult.PdfUnavailable, null, "Не удалось сформировать PDF: сервис документов временно недоступен. Попробуйте позже.");
        }

        var now = DateTime.UtcNow;
        var reportId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var storageKey = StorageKeyFactory.Create(attachmentId);

        // Блоб шифруется целиком до записи — в хранилище только шифротекст (как у вложений).
        using var encrypted = new MemoryStream();
        using (var plain = new MemoryStream(pdf))
        {
            await fileCipher.EncryptAsync(plain, encrypted, ct);
        }
        var encryptedSize = encrypted.Length;
        encrypted.Position = 0;
        await storage.SaveAsync(storageKey, encrypted, encryptedSize, "application/octet-stream", ct);

        var report = new DoctorReport
        {
            Id = reportId,
            OwnerUserId = userId,
            SubjectUserId = subject.IsSelf ? null : subject.UserId,
            SubjectFamilyDependentId = subject.DependentId,
            PeriodFrom = request.PeriodFrom,
            PeriodTo = request.PeriodTo,
            CreatedAt = now,
            IncludeLabs = request.IncludeLabs,
            IncludeAiSummaries = request.IncludeAiSummaries,
            IncludeMedications = request.IncludeMedications,
            IncludeVisits = request.IncludeVisits,
            IncludeMeasurements = request.IncludeMeasurements,
            IncludeSymptomsNotes = request.IncludeSymptomsNotes,
            IncludeVaccinations = request.IncludeVaccinations,
            PageCount = PdfPageCounter.Count(pdf),
            Recipient = Normalize(request.Recipient),
            PatientComment = Normalize(request.PatientComment),
            PatientSnapshotJson = JsonSerializer.Serialize(
                new ReportSnapshot(model.Patient.FullName, model.Patient.Sex, model.Patient.BirthDate, DoctorReportHtmlRenderer.Sections(model), model.Patient.IsPet), Json),
        };
        if (request.ShareDays is { } days) IssueLink(report, days, now);

        db.DoctorReports.Add(report);
        db.FileAttachments.Add(new FileAttachment
        {
            Id = attachmentId,
            OwnerType = FileOwnerType.DoctorReport,
            OwnerId = reportId,
            StorageKey = storageKey,
            FileName = "doctor-report.pdf",
            ContentType = "application/pdf",
            SizeBytes = pdf.Length,
            IsEncrypted = true,
            KeyId = keyRing.ActiveKeyId,
            UploadedAt = now,
            // Превью не нужно: PDF открывается своим вьюером, а видимости через AttachmentService у отчёта нет.
            PreviewStatus = AttachmentPreviewStatus.Unsupported,
        });
        var patientUserId = report.SubjectUserId ?? userId;
        audit.Enqueue(userId, MedicalAccessAction.DoctorReportCreated, ownerUserId: patientUserId);
        if (request.ShareDays is not null) audit.Enqueue(userId, MedicalAccessAction.DoctorReportLinkIssued, ownerUserId: patientUserId);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Блоб без строки в БД недоступен и бесполезен — убираем, чтобы не копить сирот.
            await TryDeleteBlobAsync(storageKey);
            throw;
        }

        logger.LogInformation("Отчёт для врача {ReportId} создан пользователем {UserId} ({Pages} стр.)", reportId, userId, report.PageCount);
        if (report.SubjectUserId is { } subjectUserId) await NotifySubjectAsync(report, subjectUserId, authorName, ct);
        return (DoctorReportResult.Success, ToDto(report, now, userId, new Dictionary<Guid, string>()), null);
    }

    /// <summary>PDF для автора или взрослого пациента (скачивание/просмотр в приложении).</summary>
    public async Task<(Stream Content, string FileName)?> OpenOwnerPdfAsync(Guid userId, Guid reportId, CancellationToken ct = default)
    {
        var report = await db.DoctorReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && (r.OwnerUserId == userId || r.SubjectUserId == userId), ct);
        if (report is null) return null;
        var content = await OpenPdfAsync(report, ct);
        return content is null ? null : (content, FileNameFor(report));
    }

    /// <summary>Выдаёт ссылку: у активной продлевает срок (тот же токен), в остальных случаях (нет / истекла /
    /// отозвана) выпускает новую — старый токен при этом мёртв.</summary>
    public async Task<(DoctorReportResult Result, DoctorReportDto? Item, string? Error)> ShareAsync(
        Guid userId, Guid reportId, int days, CancellationToken ct = default)
    {
        if (!AllowedShareDays.Contains(days))
            return (DoctorReportResult.Invalid, null, "Срок ссылки — 7, 14 или 30 дней.");

        var report = await db.DoctorReports.FirstOrDefaultAsync(r => r.Id == reportId && r.OwnerUserId == userId, ct);
        if (report is null) return (DoctorReportResult.NotFound, null, null);

        var now = DateTime.UtcNow;
        if (LinkStatus(report, now) == DoctorReportLinkStatus.Active)
            report.ShareExpiresAt = now.AddDays(days);
        else
            IssueLink(report, days, now);

        audit.Enqueue(userId, MedicalAccessAction.DoctorReportLinkIssued, ownerUserId: report.SubjectUserId ?? userId);
        await db.SaveChangesAsync(ct);
        return (DoctorReportResult.Success, ToDto(report, now, userId, new Dictionary<Guid, string>()), null);
    }

    /// <summary>Отзывает ссылку — автор или взрослый пациент. Идемпотентно: если ссылки уже нет — успех без изменений.</summary>
    public async Task<(DoctorReportResult Result, DoctorReportDto? Item)> RevokeAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        var report = await db.DoctorReports
            .FirstOrDefaultAsync(r => r.Id == reportId && (r.OwnerUserId == userId || r.SubjectUserId == userId), ct);
        if (report is null) return (DoctorReportResult.NotFound, null);

        var now = DateTime.UtcNow;
        if (report.ShareTokenHash is not null)
        {
            // Токен и хеш стираются: старая ссылка перестаёт находиться вообще, а не только «истекает».
            report.ShareTokenHash = null;
            report.ShareToken = null;
            report.ShareRevokedAt = now;
            audit.Enqueue(userId, MedicalAccessAction.DoctorReportLinkRevoked, ownerUserId: report.SubjectUserId ?? userId);
            await db.SaveChangesAsync(ct);
        }
        var authors = report.OwnerUserId == userId
            ? new Dictionary<Guid, string>()
            : await AuthorNamesAsync([report.OwnerUserId], ct);
        return (DoctorReportResult.Success, ToDto(report, now, userId, authors));
    }

    public async Task<DoctorReportResult> DeleteAsync(Guid userId, Guid reportId, CancellationToken ct = default)
    {
        var report = await db.DoctorReports.FirstOrDefaultAsync(r => r.Id == reportId && r.OwnerUserId == userId, ct);
        if (report is null) return DoctorReportResult.NotFound;

        var attachments = await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.DoctorReport && a.OwnerId == reportId)
            .ToListAsync(ct);
        var keys = attachments.Select(a => a.StorageKey).ToList();

        db.FileAttachments.RemoveRange(attachments);
        db.DoctorReports.Remove(report);
        await db.SaveChangesAsync(ct);

        // Файлы — после коммита БД: осиротевший шифроблоб без строки нечитаем.
        foreach (var key in keys) await TryDeleteBlobAsync(key);
        return DoctorReportResult.Success;
    }

    // ---- Публичный доступ (врач, без аккаунта) ----

    /// <summary>Метаданные страницы врача. null — токен неизвестен, истёк или отозван (все случаи одинаковы).</summary>
    public async Task<PublicReportMeta?> GetPublicMetaAsync(string token, CancellationToken ct = default)
    {
        var report = await FindActiveByTokenAsync(token, ct);
        if (report is null) return null;

        ReportSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ReportSnapshot>(report.PatientSnapshotJson, Json);
        }
        catch (JsonException)
        {
            snapshot = null;
        }
        if (snapshot is null) return null;

        var age = snapshot.BirthDate is { } b ? AgeOn(b, DateOnly.FromDateTime(DateTime.UtcNow)) : (int?)null;
        return new PublicReportMeta(
            snapshot.FullName, snapshot.Sex, age, snapshot.BirthDate,
            report.PeriodFrom, report.PeriodTo, report.CreatedAt, report.ShareExpiresAt!.Value,
            report.PageCount, snapshot.Sections, snapshot.IsPet);
    }

    /// <summary>Отдаёт PDF по токену и фиксирует открытие: счётчик, время и аудит (смотрящий анонимен).</summary>
    public async Task<(Stream Content, string FileName)?> OpenPublicPdfAsync(string token, CancellationToken ct = default)
    {
        var report = await FindActiveByTokenAsync(token, ct);
        if (report is null) return null;

        var content = await OpenPdfAsync(report, ct);
        if (content is null) return null;

        report.ShareViewCount++;
        report.ShareLastViewedAt = DateTime.UtcNow;
        audit.Enqueue(Guid.Empty, MedicalAccessAction.DoctorReportViewed, ownerUserId: report.OwnerUserId);
        await db.SaveChangesAsync(ct);
        return (content, FileNameFor(report));
    }

    // ---- Внутреннее ----

    private async Task<DoctorReport?> FindActiveByTokenAsync(string token, CancellationToken ct)
    {
        // Токен — 32 случайных байта в base64url (43 символа); всё остальное отсекаем до похода в БД.
        if (string.IsNullOrEmpty(token) || token.Length > 64) return null;

        var hash = TokenHasher.Hash(token);
        var report = await db.DoctorReports.FirstOrDefaultAsync(r => r.ShareTokenHash == hash, ct);
        return report is not null && LinkStatus(report, DateTime.UtcNow) == DoctorReportLinkStatus.Active ? report : null;
    }

    private async Task<Stream?> OpenPdfAsync(DoctorReport report, CancellationToken ct)
    {
        var attachment = await db.FileAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerType == FileOwnerType.DoctorReport && a.OwnerId == report.Id, ct);
        if (attachment is null) return null;

        await using var stored = await storage.OpenReadAsync(attachment.StorageKey, ct);
        return attachment.IsEncrypted ? await fileCipher.DecryptAsync(stored, ct) : await CopyAsync(stored, ct);
    }

    private static async Task<Stream> CopyAsync(Stream source, CancellationToken ct)
    {
        var copy = new MemoryStream();
        await source.CopyToAsync(copy, ct);
        copy.Position = 0;
        return copy;
    }

    private static void IssueLink(DoctorReport report, int days, DateTime now)
    {
        // 32 случайных байта → base64url без «=»: не угадывается и безопасно лежит в пути URL.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        report.ShareToken = token;
        report.ShareTokenHash = TokenHasher.Hash(token);
        report.ShareExpiresAt = now.AddDays(days);
        report.ShareRevokedAt = null;
        report.ShareViewCount = 0;
        report.ShareLastViewedAt = null;
    }

    public static DoctorReportLinkStatus LinkStatus(DoctorReport r, DateTime now)
    {
        if (r.ShareTokenHash is null) return r.ShareRevokedAt is null ? DoctorReportLinkStatus.None : DoctorReportLinkStatus.Revoked;
        return r.ShareExpiresAt is { } exp && exp > now ? DoctorReportLinkStatus.Active : DoctorReportLinkStatus.Expired;
    }

    /// <summary>«Иванов И.И.» по Id авторов — подпись «Составил(а)» в списке пациента и в PDF.</summary>
    private async Task<Dictionary<Guid, string>> AuthorNamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.LastName, u.FirstName, u.MiddleName }).ToListAsync(ct);
        return users.ToDictionary(
            u => u.Id,
            u => PersonName.FormatOrDefault(u.LastName, u.FirstName, u.MiddleName, PersonNameStyle.Initials, "Член семьи"));
    }

    /// <summary>Пациенту — что о нём составили отчёт. Без медицинских подробностей: таблица уведомлений не
    /// шифруется, а Telegram пересылает текст дословно. Сбой отправки не отменяет уже созданный отчёт.</summary>
    private async Task NotifySubjectAsync(DoctorReport report, Guid subjectUserId, string? authorName, CancellationToken ct)
    {
        var who = authorName ?? "Член семьи";
        try
        {
            await notifications.NotifyAsync(
                [subjectUserId], null, NotificationType.DoctorReportAboutYou,
                "Отчёт для врача о вас",
                $"{who} составил(а) отчёт для врача с вашими данными. Он в разделе «Здоровье → Отчёты врачу»: там его можно открыть и отозвать ссылку.",
                report.Id, _ => $"doctor-report-about-you:{report.Id}", ct, NotificationRelatedKind.DoctorReport);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось уведомить пациента {UserId} об отчёте {ReportId}", subjectUserId, report.Id);
        }
    }

    private static DoctorReportDto ToDto(DoctorReport r, DateTime now, Guid viewerId, IReadOnlyDictionary<Guid, string> authorNames)
    {
        var status = LinkStatus(r, now);
        var blocks = new DoctorReportBlocksDto(
            r.IncludeLabs, r.IncludeAiSummaries, r.IncludeMedications, r.IncludeVisits, r.IncludeMeasurements,
            r.IncludeSymptomsNotes, r.IncludeVaccinations);
        var blockCount = new[]
            {
                r.IncludeLabs, r.IncludeAiSummaries, r.IncludeMedications, r.IncludeVisits, r.IncludeMeasurements,
                r.IncludeSymptomsNotes, r.IncludeVaccinations,
            }
            .Count(x => x);
        var snapshot = ReadSnapshot(r);
        var kind = r.SubjectFamilyDependentId is not null ? DoctorReportSubjectKind.Dependent
            : r.SubjectUserId is not null ? DoctorReportSubjectKind.User
            : DoctorReportSubjectKind.Self;
        var isOwner = r.OwnerUserId == viewerId;
        return new DoctorReportDto(
            r.Id, r.PeriodFrom, r.PeriodTo, r.CreatedAt, r.PageCount, blockCount,
            // Адресат — личная пометка автора («терапевт Смирнова»), пациенту её не показываем.
            isOwner ? r.Recipient : null,
            blocks,
            new DoctorReportLinkDto(status, r.ShareToken, r.ShareExpiresAt, r.ShareRevokedAt, r.ShareViewCount, r.ShareLastViewedAt),
            kind,
            r.SubjectFamilyDependentId ?? r.SubjectUserId,
            snapshot?.FullName,
            snapshot?.IsPet ?? false,
            isOwner ? null : authorNames.GetValueOrDefault(r.OwnerUserId, "Член семьи"),
            isOwner);
    }

    private static ReportSnapshot? ReadSnapshot(DoctorReport r)
    {
        if (string.IsNullOrEmpty(r.PatientSnapshotJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReportSnapshot>(r.PatientSnapshotJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ValidatePeriod(DateOnly from, DateOnly to)
    {
        if (from > to) return "Начало периода позже его конца.";
        if (to.DayNumber - from.DayNumber > MaxPeriodDays) return "Период не должен превышать двух лет.";
        if (to > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)) return "Конец периода не может быть в будущем.";
        return null;
    }

    private static string? Validate(CreateDoctorReportRequest r)
    {
        var period = ValidatePeriod(r.PeriodFrom, r.PeriodTo);
        if (period is not null) return period;
        if (!(r.IncludeLabs || r.IncludeAiSummaries || r.IncludeMedications || r.IncludeVisits || r.IncludeMeasurements
              || r.IncludeSymptomsNotes || r.IncludeVaccinations))
            return "Выберите хотя бы один блок для отчёта.";
        if (r.PatientComment is { Length: > MaxCommentLength }) return "Слишком длинный текст жалоб.";
        if (r.Recipient is { Length: > MaxRecipientLength }) return "Слишком длинная подпись «для кого».";
        if (r.ShareDays is { } d && !AllowedShareDays.Contains(d)) return "Срок ссылки — 7, 14 или 30 дней.";
        return null;
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int? AgeOn(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        if (on < birth.AddYears(age)) age--;
        return age is < 0 or > 130 ? null : age;
    }

    private static string FileNameFor(DoctorReport r) => $"otchet-dlya-vracha-{r.PeriodFrom:yyyyMMdd}-{r.PeriodTo:yyyyMMdd}.pdf";

    private async Task TryDeleteBlobAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось удалить блоб отчёта {StorageKey} — потребуется ручная зачистка", key);
        }
    }
}
