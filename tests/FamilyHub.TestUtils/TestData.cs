using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;

namespace FamilyHub.TestUtils;

/// <summary>Фабрики "сырых" сущностей с разумными дефолтами — для сидинга в тестах без лишнего шума.</summary>
public static class TestData
{
    private static long _nextTelegramId = 1;

    public static User NewUser(string? lastName = "Testov", string? firstName = "Test") => new()
    {
        Id = Guid.NewGuid(),
        TelegramId = Interlocked.Increment(ref _nextTelegramId),
        LastName = lastName,
        FirstName = firstName,
        // ~полгода от "сегодня" (не фиксированная календарная дата) — гарантирует, что дефолтный
        // ДР никогда не попадёт в окно ReminderScanJob.BirthdayWarningDays (7 дней по умолчанию,
        // до нескольких десятков дней в редких тестах с явным override) ни в один день года,
        // включая день прогона теста. Фиксированная дата (напр. 1 января) рано или поздно
        // случайно совпала бы с окном и сделала бы несвязанные тесты флаки.
        BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30).AddDays(183)),
        Gender = Gender.Male,
        CreatedAt = DateTime.UtcNow,
    };

    public static Family NewFamily(string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name ?? "Test Family",
        PlanType = PlanType.Free,
        CreatedAt = DateTime.UtcNow,
    };

    public static FamilyMember NewMember(
        Guid familyId, Guid userId, FamilyRole role = FamilyRole.Member, MemberStatus status = MemberStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        FamilyId = familyId,
        UserId = userId,
        Role = role,
        Status = status,
        JoinedAt = DateTime.UtcNow,
    };

    public static FamilyInvite NewInvite(
        Guid familyId, Guid createdByUserId, Guid? targetUserId = null,
        FamilyRole assignedRole = FamilyRole.Member, int maxUses = 1, DateTime? expiresAt = null, bool isRevoked = false) => new()
    {
        Id = Guid.NewGuid(),
        FamilyId = familyId,
        CreatedByUserId = createdByUserId,
        Code = Guid.NewGuid().ToString("N"),
        TargetUserId = targetUserId,
        AssignedRole = assignedRole,
        MaxUses = maxUses,
        UsedCount = 0,
        ExpiresAt = expiresAt,
        IsRevoked = isRevoked,
        CreatedAt = DateTime.UtcNow,
    };

    public static Medkit NewMedkit(Guid familyId, Guid createdByUserId, string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        FamilyId = familyId,
        Name = name ?? "Test Medkit",
        CreatedByUserId = createdByUserId,
        CreatedAt = DateTime.UtcNow,
    };

    public static Medication NewMedication(Guid medkitId, Guid familyId, Guid createdByUserId, DateOnly? expiryDate = null) => new()
    {
        Id = Guid.NewGuid(),
        MedkitId = medkitId,
        FamilyId = familyId,
        Name = "Test Medication",
        ExpiryDate = expiryDate,
        CreatedByUserId = createdByUserId,
        CreatedAt = DateTime.UtcNow,
    };

    public static Birthday NewBirthday(Guid familyId, DateOnly? date = null) => new()
    {
        Id = Guid.NewGuid(),
        FamilyId = familyId,
        PersonName = "Test Person",
        Date = date ?? new DateOnly(2000, 1, 1),
    };

    public static MedicalRecord NewMedicalRecord(Guid ownerUserId, MedicalRecordKind kind = MedicalRecordKind.Analysis) => new()
    {
        Id = Guid.NewGuid(),
        OwnerUserId = ownerUserId,
        Kind = kind,
        RecordDate = DateOnly.FromDateTime(DateTime.UtcNow),
        CreatedAt = DateTime.UtcNow,
    };

    public static Notification NewNotification(
        Guid userId, Guid familyId, string dedupKey, NotificationType type = NotificationType.MedicationExpiringSoon) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        FamilyId = familyId,
        Type = type,
        Title = "Test",
        Body = "Test body",
        RelatedEntityId = Guid.NewGuid(),
        DedupKey = dedupKey,
        CreatedAt = DateTime.UtcNow,
    };
}
