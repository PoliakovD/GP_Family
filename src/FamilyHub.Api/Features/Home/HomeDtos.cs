using FamilyHub.Modules.Birthdays.Birthdays;

namespace FamilyHub.Api.Features.Home;

/// <summary>Просроченное или истекающее лекарство — редизайн v2, блок «Требует внимания».
/// Severity считается на бэке (тот же порог ExpiryWarningDays, что и у ReminderScanJob) —
/// фронт не дублирует пороги. "expired" | "expiring".</summary>
public record HomeMedicationAlert(
    Guid MedicationId, Guid MedkitId, string MedkitName,
    Guid FamilyId, string FamilyName,
    string Name, DateOnly ExpiryDate, int DaysLeft, string Severity);

/// <summary>Заявка на вступление в семью, где текущий пользователь — Admin. ФИО тремя полями —
/// тот же формат, что PendingMember/CurrentFamilyMember (см. shared/util/person-name.ts).</summary>
public record HomeJoinRequest(
    Guid FamilyId, string FamilyName, Guid UserId,
    string? LastName, string? FirstName, string? MiddleName,
    string? Username, DateTime RequestedAt);

/// <summary>Ближайший день рождения (любой из трёх источников — см. BirthdayService.GetForFamilyAsync).</summary>
public record HomeBirthdayItem(
    Guid FamilyId, string FamilyName, string PersonName,
    DateOnly Date, int DaysUntil, int TurningAge, BirthdaySource Source);

/// <summary>Блок «В порядке» — одна строка чипов, без отдельных запросов с фронта.</summary>
public record HomeOkChips(
    int MedicationsInDate, int MedicationsTotal,
    int AnalysesTotal, int AnalysesAbnormal,
    bool PushEnabled);

public record HomeSummaryResponse(
    string? GreetingName,
    DateOnly Today,
    int AttentionTotal,
    Guid? PrimaryFamilyId,
    string? PrimaryFamilyName,
    IReadOnlyList<HomeMedicationAlert> Medications,
    IReadOnlyList<HomeJoinRequest> JoinRequests,
    IReadOnlyList<HomeBirthdayItem> Birthdays,
    HomeOkChips Ok,
    int UnreadNotifications);
