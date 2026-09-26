using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.HealthNotes;
using FamilyHub.Modules.Medical.MedicationCourses;
using FluentAssertions;
using Xunit;

namespace FamilyHub.IntegrationTests;

/// <summary>Курсы приёма лекарств (ADR-0015): изоляция по владельцу, отметка «по необходимости» с записью в
/// дневник, часовой пояс и анонимные кнопки push (токен вместо сессии).</summary>
public class MedicationCoursesApiTests(FamilyHubWebFactory factory) : IntegrationTestBase(factory)
{
    private static object AsNeededCourse(string name = "Нурофен 200 мг") => new
    {
        dependentId = (Guid?)null,
        drugName = name,
        schedule = new { mode = (int)DoseScheduleMode.AsNeeded, maxPerDay = 2 },
        food = (int)FoodRelation.Any,
        unit = (int)DoseUnit.Tablet,
        startDate = DateOnly.FromDateTime(DateTime.UtcNow),
        endDate = (DateOnly?)null,
        medicationId = (Guid?)null,
        writeOff = false,
        repeatAfterMinutes = (int?)null,
        missedAfterMinutes = 120,
        lowStockDays = 5,
        sourceMedicalRecordId = (Guid?)null,
        sourcePrescriptionIndex = (int?)null,
        prescriptionText = (string?)null,
        notes = (string?)null,
    };

    [Fact]
    public async Task Create_ThenGet_OwnerSeesIt_StrangerGetsNotFound()
    {
        var owner = ClientAs(FreshTelegramId());
        var stranger = ClientAs(FreshTelegramId());

        var created = await owner.PostAsJsonAsync("/api/medication-courses", AsNeededCourse());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var course = await created.Content.ReadFromJsonAsync<CourseDetailDto>(JsonOpts);

        (await owner.GetAsync($"/api/medication-courses/{course!.Summary.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await stranger.GetAsync($"/api/medication-courses/{course.Summary.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"/api/medication-courses/{course.Summary.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var theirs = await stranger.GetFromJsonAsync<List<CourseSummaryDto>>("/api/medication-courses", JsonOpts);
        theirs.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_InvalidSchedule_Returns400()
    {
        var owner = ClientAs(FreshTelegramId());
        var body = new
        {
            dependentId = (Guid?)null,
            drugName = "Сорбифер",
            schedule = new { mode = (int)DoseScheduleMode.EveryNHours, intervalHours = 5, intervalStart = "08:00:00" },
            food = 0, unit = 0, startDate = DateOnly.FromDateTime(DateTime.UtcNow), missedAfterMinutes = 120, lowStockDays = 5,
        };

        (await owner.PostAsJsonAsync("/api/medication-courses", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AsNeeded_TakeWritesDiaryNote_AndOverLimitNeedsConfirmation()
    {
        var owner = ClientAs(FreshTelegramId());
        var course = await (await owner.PostAsJsonAsync("/api/medication-courses", AsNeededCourse()))
            .Content.ReadFromJsonAsync<CourseDetailDto>(JsonOpts);
        var id = course!.Summary.Id;

        (await owner.PostAsJsonAsync($"/api/medication-courses/{id}/prn", new { force = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/medication-courses/{id}/prn", new { force = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/medication-courses/{id}/prn", new { force = false })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.PostAsJsonAsync($"/api/medication-courses/{id}/prn", new { force = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        var notes = await owner.GetFromJsonAsync<List<HealthNoteDto>>("/api/health-notes", JsonOpts);
        notes!.Count(n => n.Kind == HealthNoteKind.MedicationIntake && n.Title == "Нурофен 200 мг").Should().Be(3);

        var today = await owner.GetFromJsonAsync<TodayResponse>("/api/medication-courses/today", JsonOpts);
        today!.AsNeeded.Should().ContainSingle(a => a.CourseId == id && a.TakenToday == 3);
    }

    [Fact]
    public async Task PauseThenComplete_ChangesListMembership()
    {
        var owner = ClientAs(FreshTelegramId());
        var course = await (await owner.PostAsJsonAsync("/api/medication-courses", AsNeededCourse()))
            .Content.ReadFromJsonAsync<CourseDetailDto>(JsonOpts);
        var id = course!.Summary.Id;

        (await owner.PostAsync($"/api/medication-courses/{id}/pause", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PostAsync($"/api/medication-courses/{id}/complete", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await owner.GetFromJsonAsync<List<CourseSummaryDto>>("/api/medication-courses", JsonOpts)).Should().BeEmpty();
        (await owner.GetFromJsonAsync<List<CourseSummaryDto>>("/api/medication-courses?status=completed", JsonOpts))
            .Should().ContainSingle(c => c.Id == id);
    }

    [Fact]
    public async Task TimeZone_ValidIsStored_UnknownIsRejected()
    {
        var owner = ClientAs(FreshTelegramId());

        (await owner.PutAsJsonAsync("/api/account/time-zone", new { timeZoneId = "Europe/Moscow" })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await owner.PutAsJsonAsync("/api/account/time-zone", new { timeZoneId = "Mars/Base" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var settings = await owner.GetFromJsonAsync<ReminderSettingsResponse>("/api/medication-reminders/settings", JsonOpts);
        settings!.TimeZoneId.Should().Be("Europe/Moscow");
    }

    [Fact]
    public async Task DoseAction_UnknownToken_Is404_WithoutSession_AndBadActionIs400()
    {
        var anonymous = AnonymousClient();

        var notFound = await anonymous.GetAsync("/api/public/dose-actions/no-such-token?a=taken");
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        notFound.Headers.CacheControl!.NoStore.Should().BeTrue();

        (await anonymous.GetAsync("/api/public/dose-actions/no-such-token?a=explode")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anonymous.GetAsync("/api/public/dose-actions/no-such-token")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReminderSettings_MyWatchers_RejectsNonFamilyMember()
    {
        var owner = ClientAs(FreshTelegramId());

        var response = await owner.PutAsJsonAsync("/api/medication-reminders/my-watchers", new { userIds = new[] { Guid.NewGuid() } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuietHours_RoundTrip()
    {
        var owner = ClientAs(FreshTelegramId());

        (await owner.PutAsJsonAsync("/api/medication-reminders/quiet-hours", new { from = "23:00:00", to = "07:00:00" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/medication-reminders/settings", JsonOpts);
        settings.GetProperty("quietHoursFrom").GetString().Should().Be("23:00:00");
    }
}
