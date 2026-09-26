using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Modules.Medical.HealthNotes;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.TestUtils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyHub.UnitTests.Modules.Medical;

/// <summary>Общая обвязка тестов курсов приёма: сервисы на реальном SQLite и сидеры «семья + подопечный +
/// аптечка». Часовой пояс пользователей — UTC, чтобы времена расписания не зависели от пояса машины.</summary>
public abstract class MedicationCourseTestBase : SqliteTestBase
{
    protected readonly MedicationCourseService Courses;
    protected readonly DoseService Doses;
    protected readonly MedicationTodayService Today;
    protected readonly MedicationReminderSettingsService Reminders;
    protected readonly MedicationCourseAccess Access;

    protected MedicationCourseTestBase()
    {
        var family = new FamilyAccessService(Db, NullLogger<FamilyAccessService>.Instance);
        Access = new MedicationCourseAccess(Db, family);
        var subjects = new CourseSubjects(Db);
        var stock = new MedkitStockService(Db, family);
        Courses = new MedicationCourseService(Db, Access, subjects, stock, new MedicalAuditWriter(Db),
            NullLogger<MedicationCourseService>.Instance);
        Doses = new DoseService(Db, Access, stock, new HealthNoteService(Db, NullLogger<HealthNoteService>.Instance),
            NullLogger<DoseService>.Instance);
        Today = new MedicationTodayService(Db, Access, subjects, stock);
        Reminders = new MedicationReminderSettingsService(Db, family, subjects);
    }

    /// <summary>Семья с админом (UTC-пояс) — базовая сцена большинства тестов.</summary>
    protected (Family Family, User Admin) SeedFamily()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.TimeZoneId = "UTC";
        Db.SaveChanges();
        return (family, admin);
    }

    protected User AddMemberUtc(Guid familyId, FamilyRole role = FamilyRole.Member, MemberStatus status = MemberStatus.Active)
    {
        var user = Db.AddMember(familyId, role, status);
        user.TimeZoneId = "UTC";
        Db.SaveChanges();
        return user;
    }

    protected FamilyDependent AddDependent(Guid familyId, Guid createdBy, string name = "Мама")
    {
        var d = new FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = familyId, FirstName = name, Gender = Gender.Female,
            CreatedByUserId = createdBy, CreatedAt = DateTime.UtcNow,
        };
        Db.FamilyDependents.Add(d);
        Db.SaveChanges();
        return d;
    }

    /// <summary>Препарат в аптечке семьи с количеством в DataJson.</summary>
    protected Medication AddMedication(Guid familyId, Guid userId, string? quantity = "22 таб.", string name = "Сорбифер")
    {
        var kit = TestData.NewMedkit(familyId, userId);
        Db.Medkits.Add(kit);
        var med = TestData.NewMedication(kit.Id, familyId, userId);
        med.Name = name;
        med.DataJson = quantity is null ? null : System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["quantity"] = quantity });
        Db.Medications.Add(med);
        Db.SaveChanges();
        return med;
    }

    protected static DateTime MinuteFromNow(int minutes)
    {
        var t = DateTime.UtcNow.AddMinutes(minutes);
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc);
    }

    /// <summary>Раз в день в заданный момент (UTC) — приём наступает «ровно тогда».</summary>
    protected static DoseSchedule OnceAt(DateTime utc, decimal units = 1) =>
        new(DoseScheduleMode.TimesPerDay, Times: [new DoseTime(TimeOnly.FromDateTime(utc), units)]);

    protected static DoseSchedule AsNeeded(int max = 2) => new(DoseScheduleMode.AsNeeded, MaxPerDay: max);

    protected static CourseRequest Request(
        DoseSchedule? schedule = null, Guid? dependentId = null, Guid? medicationId = null, bool writeOff = false,
        string name = "Сорбифер Дурулес 100 мг", DateOnly? start = null, DateOnly? end = null) =>
        new(dependentId, name,
            schedule ?? new DoseSchedule(DoseScheduleMode.TimesPerDay,
                Times: [new DoseTime(new TimeOnly(8, 0), 1), new DoseTime(new TimeOnly(20, 0), 1)]),
            FoodRelation.Before, DoseUnit.Tablet,
            start ?? DateOnly.FromDateTime(DateTime.UtcNow), end, medicationId, writeOff,
            RepeatAfterMinutes: 15, MedicationCourseRules.DefaultMissedAfterMinutes, MedicationCourseRules.DefaultLowStockDays,
            null, null, null, null);

    /// <summary>Курс, созданный напрямую в БД — с полным контролем над EffectiveFromUtc и статусом.</summary>
    protected MedicationCourse SeedCourse(
        Guid? subjectUserId, Guid? dependentId, Guid? familyId, Guid createdBy, DoseSchedule schedule,
        DateTime? effectiveFrom = null, MedicationCourseStatus status = MedicationCourseStatus.Active,
        Guid? medicationId = null, bool writeOff = false, string name = "Сорбифер")
    {
        var course = new MedicationCourse
        {
            Id = Guid.NewGuid(), SubjectUserId = subjectUserId, FamilyDependentId = dependentId, FamilyId = familyId,
            CreatedByUserId = createdBy, DrugName = name, ScheduleJson = MedicationCourseRules.SerializeSchedule(schedule),
            Food = FoodRelation.Any, DoseUnit = DoseUnit.Tablet, StartDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)),
            TimeZoneId = "UTC", Status = status, EffectiveFromUtc = effectiveFrom ?? DateTime.UtcNow.AddDays(-30),
            MedicationId = medicationId, WriteOffEnabled = writeOff, MissedAfterMinutes = 120, LowStockDays = 5,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.MedicationCourses.Add(course);
        Db.SaveChanges();
        return course;
    }

    /// <summary>Свежее значение из БД: списание идёт через ExecuteUpdate и трекер не обновляет.</summary>
    protected string? QuantityOf(Guid medicationId) =>
        MedkitStockService.ReadQuantity(Db.Medications.AsNoTracking().Single(m => m.Id == medicationId).DataJson);
}
