using FamilyHub.Api.Features.Account;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Domain.MedicationCourses;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Features.Account;

public class ProfileTimeZoneTests : SqliteTestBase
{
    private readonly ProfileService _sut;

    public ProfileTimeZoneTests() => _sut = new ProfileService(Db);

    private MedicationCourse SeedCourse(Guid userId, string tz, MedicationCourseStatus status = MedicationCourseStatus.Active)
    {
        var course = new MedicationCourse
        {
            Id = Guid.NewGuid(), SubjectUserId = userId, CreatedByUserId = userId, DrugName = "Сорбифер",
            ScheduleJson = MedicationCourseRules.SerializeSchedule(new DoseSchedule(DoseScheduleMode.AsNeeded, MaxPerDay: 2)),
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneId = tz, Status = status,
            EffectiveFromUtc = DateTime.UtcNow.AddDays(-10), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.MedicationCourses.Add(course);
        Db.SaveChanges();
        return course;
    }

    [Fact]
    public async Task SetTimeZone_StoresIt_AndMovesOwnActiveCoursesFromNow()
    {
        var user = Db.AddUser();
        var active = SeedCourse(user.Id, "Europe/Moscow");
        var completed = SeedCourse(user.Id, "Europe/Moscow", MedicationCourseStatus.Completed);

        (await _sut.SetTimeZoneAsync(user.Id, "Asia/Yekaterinburg")).Should().BeTrue();

        Db.Users.AsNoTracking().Single(u => u.Id == user.Id).TimeZoneId.Should().Be("Asia/Yekaterinburg");
        var movedCourse = Db.MedicationCourses.AsNoTracking().Single(c => c.Id == active.Id);
        movedCourse.TimeZoneId.Should().Be("Asia/Yekaterinburg");
        movedCourse.EffectiveFromUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10)); // без «догоняющих» пропусков
        Db.MedicationCourses.AsNoTracking().Single(c => c.Id == completed.Id).TimeZoneId.Should().Be("Europe/Moscow");
    }

    [Fact]
    public async Task SetTimeZone_Unchanged_DoesNotTouchCourses()
    {
        var user = Db.AddUser();
        user.TimeZoneId = "Europe/Moscow";
        Db.SaveChanges();
        var course = SeedCourse(user.Id, "Europe/Moscow");
        var effective = course.EffectiveFromUtc;

        (await _sut.SetTimeZoneAsync(user.Id, "Europe/Moscow")).Should().BeTrue();

        Db.MedicationCourses.AsNoTracking().Single().EffectiveFromUtc.Should().BeCloseTo(effective, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mars/Olympus_Mons")]
    public async Task SetTimeZone_UnknownZone_IsRejected(string? zone)
    {
        var user = Db.AddUser();

        (await _sut.SetTimeZoneAsync(user.Id, zone)).Should().BeFalse();

        Db.Users.AsNoTracking().Single(u => u.Id == user.Id).TimeZoneId.Should().BeNull();
    }
}
