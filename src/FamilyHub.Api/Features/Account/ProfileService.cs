using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Domain.ValueObjects;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Account;

public record UpdateProfileRequest(string LastName, string FirstName, string? MiddleName, DateOnly BirthDate, Gender Gender);

public enum UpdateProfileResult { Success, InvalidProfile }

public record SetTimeZoneRequest(string? TimeZoneId);

/// <summary>
/// Единственный путь записи ФИО/ДР/пола ПОСЛЕ создания User (identity rework) — и настройками
/// (SettingsProfileComponent), и первичным экраном сбора профиля после Telegram-привязки
/// (ProfileSetupComponent, см. profileGuard). При регистрации PWA профиль пишется отдельно,
/// сразу в PwaAuthService.ConfirmRegistrationAsync — здесь только апдейт уже существующего User.
/// </summary>
public class ProfileService(AppDbContext db)
{
    public async Task<UpdateProfileResult> UpdateAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        if (!PersonName.IsValidPart(request.LastName) || !PersonName.IsValidPart(request.FirstName)
            || !PersonName.IsValidOptionalPart(request.MiddleName) || !PersonName.IsValidBirthDate(request.BirthDate))
            return UpdateProfileResult.InvalidProfile;

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.LastName = request.LastName.Trim();
        user.FirstName = request.FirstName.Trim();
        user.MiddleName = string.IsNullOrWhiteSpace(request.MiddleName) ? null : request.MiddleName.Trim();
        user.BirthDate = request.BirthDate;
        user.Gender = request.Gender;

        await db.SaveChangesAsync(ct);
        return UpdateProfileResult.Success;
    }

    /// <summary>
    /// Часовой пояс пользователя — присылает браузер (Intl). От него зависит, что значит «8:00» в курсах
    /// приёма лекарств и «сегодня» в интерфейсе. При смене пояса свои курсы переводятся на новый (время
    /// приёма остаётся «настенным») и отсчитываются заново с этого момента — иначе после перелёта задним
    /// числом появились бы «пропущенные» приёмы. false — пояс серверу неизвестен.
    /// </summary>
    public async Task<bool> SetTimeZoneAsync(Guid userId, string? timeZoneId, CancellationToken ct = default)
    {
        if (!TimeZones.IsKnown(timeZoneId)) return false;

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.TimeZoneId == timeZoneId) return true;
        user.TimeZoneId = timeZoneId;

        var now = DateTime.UtcNow;
        var courses = await db.MedicationCourses
            .Where(c => c.SubjectUserId == userId && c.Status != MedicationCourseStatus.Completed && c.TimeZoneId != timeZoneId)
            .ToListAsync(ct);
        foreach (var course in courses)
        {
            course.TimeZoneId = timeZoneId!;
            course.EffectiveFromUtc = now;
            course.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}
