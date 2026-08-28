using FamilyHub.Domain.Enums;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Birthdays.Birthdays;
using FamilyHub.Modules.Medical.MedicalRecords;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Home;

/// <summary>
/// Агрегат Главной (редизайн v2) — единственный запрос вместо 3-4, которые раньше делала бы
/// Главная по отдельности (лекарства/заявки/ДР/пуш). Живёт в FamilyHub.Api (хосте), не в одном
/// из модулей: собирает данные из Medical (лекарства, показатели) и Birthdays (дни рождения) —
/// модули по конституции проекта зависят только от Domain/Infrastructure и никогда друг от
/// друга напрямую, хост — единственное легальное место межмодульной сборки (см.
/// .claude/research/README.md, "Архитектура одной картинкой").
/// </summary>
public class HomeSummaryService(
    AppDbContext db,
    IFamilyAccessService access,
    MedicalRecordService medicalRecords,
    BirthdayService birthdays,
    IOptions<NotificationOptions> options,
    ILogger<HomeSummaryService> logger)
{
    private const int MaxMedicationAlerts = 20;
    private const int MaxJoinRequests = 20;
    private const int MaxBirthdays = 5;
    private const int BirthdayWindowDays = 30;

    public async Task<HomeSummaryResponse> BuildAsync(Guid userId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var greetingName = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.FirstName).FirstOrDefaultAsync(ct);

        var activeFamilyIds = await access.GetActiveFamilyIdsAsync(userId, ct);

        var medications = await BuildMedicationAlertsAsync(activeFamilyIds, today, ct);
        var joinRequests = await BuildJoinRequestsAsync(userId, ct);
        var birthdayItems = await BuildBirthdaysAsync(userId, activeFamilyIds, today, ct);
        var ok = await BuildOkChipsAsync(userId, activeFamilyIds, ct);
        var unreadNotifications = await GetUnreadCountAsync(userId, ct);

        // Приоритет карточек (просрочки → заявки → ДР) — на фронте, чистое отображение уже
        // отсортированных списков. Здесь только суммарный счётчик и "главная" семья дел.
        var attentionTotal = medications.Count + joinRequests.Count + birthdayItems.Count;
        var (primaryFamilyId, primaryFamilyName) = PickPrimaryFamily(medications, joinRequests, birthdayItems);

        return new HomeSummaryResponse(
            greetingName, today, attentionTotal, primaryFamilyId, primaryFamilyName,
            medications, joinRequests, birthdayItems, ok, unreadNotifications);
    }

    private async Task<List<HomeMedicationAlert>> BuildMedicationAlertsAsync(
        List<Guid> activeFamilyIds, DateOnly today, CancellationToken ct)
    {
        if (activeFamilyIds.Count == 0) return [];

        var candidates = await db.Medications.AsNoTracking()
            .Where(m => activeFamilyIds.Contains(m.FamilyId))
            .WhereExpiringOrExpired(today, options.Value.ExpiryWarningDays)
            .Select(m => new
            {
                m.Id, m.MedkitId, MedkitName = m.Medkit.Name,
                m.FamilyId, FamilyName = m.Family.Name,
                m.Name, ExpiryDate = m.ExpiryDate!.Value,
            })
            .OrderBy(m => m.ExpiryDate)
            .Take(MaxMedicationAlerts)
            .ToListAsync(ct);

        return candidates.Select(m =>
        {
            var daysLeft = m.ExpiryDate.DayNumber - today.DayNumber;
            var severity = daysLeft < 0 ? "expired" : "expiring";
            return new HomeMedicationAlert(m.Id, m.MedkitId, m.MedkitName, m.FamilyId, m.FamilyName, m.Name, m.ExpiryDate, daysLeft, severity);
        }).ToList();
    }

    private async Task<List<HomeJoinRequest>> BuildJoinRequestsAsync(Guid userId, CancellationToken ct)
    {
        // Семьи, где userId — активный Admin (не просто GetActiveFamilyIdsAsync — заявки видит
        // только Admin, см. InviteService.GetPendingMembersAsync).
        var adminFamilyIds = await db.FamilyMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MemberStatus.Active && m.Role == FamilyRole.Admin)
            .Select(m => m.FamilyId)
            .ToListAsync(ct);

        if (adminFamilyIds.Count == 0) return [];

        return await db.FamilyMembers.AsNoTracking()
            .Where(m => adminFamilyIds.Contains(m.FamilyId) && m.Status == MemberStatus.PendingApproval)
            .OrderBy(m => m.JoinedAt)
            .Take(MaxJoinRequests)
            .Select(m => new HomeJoinRequest(
                m.FamilyId, m.Family.Name, m.UserId,
                m.User.LastName, m.User.FirstName, m.User.MiddleName, m.User.Username,
                m.JoinedAt))
            .ToListAsync(ct);
    }

    private async Task<List<HomeBirthdayItem>> BuildBirthdaysAsync(
        Guid userId, List<Guid> activeFamilyIds, DateOnly today, CancellationToken ct)
    {
        if (activeFamilyIds.Count == 0) return [];

        // BirthdayService.GetForFamilyAsync — по одной семье за раз (уже склеивает Manual/Member/
        // Dependent), здесь допустимый N+1: активных семей у пользователя обычно 1-3. TODO:
        // батч-версия, если понадобится для пользователей с большим числом семей.
        var all = new List<HomeBirthdayItem>();
        foreach (var familyId in activeFamilyIds)
        {
            var (result, items) = await birthdays.GetForFamilyAsync(familyId, userId, ct);
            // GetForFamilyAsync сам перепроверяет членство — мы уже знаем, что userId активен в
            // familyId (он и есть источник activeFamilyIds), поэтому Forbidden здесь не должен
            // возникать; на случай рассинхронизации — просто пропускаем семью, не роняем весь ответ.
            if (result != BirthdayAccessResult.Success) continue;

            var familyName = await db.Families.AsNoTracking()
                .Where(f => f.Id == familyId).Select(f => f.Name).FirstOrDefaultAsync(ct) ?? string.Empty;

            foreach (var b in items)
            {
                var daysUntil = BirthdayOccurrence.DaysUntil(b.Date, today);
                if (daysUntil > BirthdayWindowDays) continue;

                var turningAge = BirthdayOccurrence.TurningAge(b.Date, today);
                all.Add(new HomeBirthdayItem(familyId, familyName, b.PersonName, b.Date, daysUntil, turningAge, b.Source));
            }
        }

        return all.OrderBy(b => b.DaysUntil).Take(MaxBirthdays).ToList();
    }

    private async Task<HomeOkChips> BuildOkChipsAsync(Guid userId, List<Guid> activeFamilyIds, CancellationToken ct)
    {
        var medicationsTotal = activeFamilyIds.Count == 0
            ? 0
            : await db.Medications.AsNoTracking().CountAsync(m => activeFamilyIds.Contains(m.FamilyId), ct);
        var medicationsExpiringOrExpired = activeFamilyIds.Count == 0
            ? 0
            : await db.Medications.AsNoTracking()
                .Where(m => activeFamilyIds.Contains(m.FamilyId))
                .WhereExpiringOrExpired(DateOnly.FromDateTime(DateTime.UtcNow), options.Value.ExpiryWarningDays)
                .CountAsync(ct);

        var visibleRecordIds = await medicalRecords.GetVisibleRecordIdsAsync(userId, MedicalRecordKind.Analysis, ct);
        var analysesAbnormal = visibleRecordIds.Count == 0
            ? 0
            : await db.LabIndicators.AsNoTracking()
                .Where(i => visibleRecordIds.Contains(i.MedicalRecordId)
                    && i.Flag != IndicatorFlag.Normal && i.Flag != IndicatorFlag.Unknown)
                .Select(i => i.MedicalRecordId)
                .Distinct()
                .CountAsync(ct);

        var pushEnabled = await db.PushSubscriptions.AsNoTracking().AnyAsync(s => s.UserId == userId, ct);

        return new HomeOkChips(
            medicationsTotal - medicationsExpiringOrExpired, medicationsTotal,
            visibleRecordIds.Count, analysesAbnormal, pushEnabled);
    }

    private Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct) =>
        db.Notifications.AsNoTracking().CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    /// <summary>Семья с наибольшим числом дел — для строки «дата · N дел в семье X». Простой
    /// подсчёт по трём уже собранным спискам, без отдельного запроса.</summary>
    private static (Guid? Id, string? Name) PickPrimaryFamily(
        List<HomeMedicationAlert> medications, List<HomeJoinRequest> joinRequests, List<HomeBirthdayItem> birthdays)
    {
        var counts = new Dictionary<Guid, (string Name, int Count)>();
        void Bump(Guid id, string name)
        {
            counts[id] = counts.TryGetValue(id, out var v) ? (name, v.Count + 1) : (name, 1);
        }

        foreach (var m in medications) Bump(m.FamilyId, m.FamilyName);
        foreach (var j in joinRequests) Bump(j.FamilyId, j.FamilyName);
        foreach (var b in birthdays) Bump(b.FamilyId, b.FamilyName);

        if (counts.Count == 0) return (null, null);
        var top = counts.OrderByDescending(kv => kv.Value.Count).First();
        return (top.Key, top.Value.Name);
    }
}
