using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Modules.Medical.MedicationCourses;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class DoseActionTokenServiceTests : MedicationCourseTestBase
{
    private readonly DoseActionTokenService _tokens;
    private readonly DoseReminderPushCustomizer _customizer;

    public DoseActionTokenServiceTests()
    {
        _tokens = new DoseActionTokenService(Db, Doses);
        _customizer = new DoseReminderPushCustomizer(Db, _tokens);
    }

    private (User User, MedicationCourse Course, MedicationDose Dose, Medication Med) SeedPendingDose(string quantity = "22 таб.")
    {
        var (family, admin) = SeedFamily();
        var med = AddMedication(family.Id, admin.Id, quantity);
        var at = MinuteFromNow(-5);
        var course = SeedCourse(admin.Id, null, family.Id, admin.Id, OnceAt(at), at.AddMinutes(-1), medicationId: med.Id, writeOff: true);
        var dose = new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Pending, RemindedAt = at, CreatedAt = at,
        };
        Db.MedicationDoses.Add(dose);
        Db.SaveChanges();
        return (admin, course, dose, med);
    }

    private Notification DueNotification(Guid userId, Guid doseId)
    {
        var n = new Notification
        {
            Id = Guid.NewGuid(), UserId = userId, Type = NotificationType.MedicationDoseDue, Title = "Время принять лекарство",
            Body = "Напоминание о приёме в 10:00", RelatedEntityId = doseId, DedupKey = Guid.NewGuid().ToString(), CreatedAt = DateTime.UtcNow,
        };
        Db.Notifications.Add(n);
        Db.SaveChanges();
        return n;
    }

    // ── Токены ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Issue_StoresOnlyHash_AndLivesPastScheduledTime()
    {
        var (admin, _, dose, _) = SeedPendingDose();

        var token = await _tokens.IssueAsync(dose, admin.Id);

        token.Length.Should().BeGreaterThan(40);
        token.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        var row = Db.DoseActionTokens.AsNoTracking().Single();
        row.TokenHash.Should().Be(DoseActionTokenService.Hash(token)).And.NotBe(token);
        row.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddHours(1));
        row.RecipientUserId.Should().Be(admin.Id);
    }

    [Fact]
    public async Task Redeem_Taken_AppliesAction_WritesOffStock_AndMarksUsed()
    {
        var (admin, _, dose, med) = SeedPendingDose();
        var token = await _tokens.IssueAsync(dose, admin.Id);

        var result = await _tokens.RedeemAsync(token, DoseAction.Taken);

        result.Should().Be(DoseTokenResult.Success);
        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Taken);
        QuantityOf(med.Id).Should().Be("21 таб.");
        Db.HealthNotes.Should().ContainSingle(); // «Принял» из уведомления делает то же, что кнопка в приложении
        var row = Db.DoseActionTokens.AsNoTracking().Single();
        row.UsedAt.Should().NotBeNull();
        row.UsedAction.Should().Be(DoseAction.Taken);
    }

    [Fact]
    public async Task Redeem_SameActionTwice_IsIdempotent_DifferentActionConflicts()
    {
        var (admin, _, dose, med) = SeedPendingDose();
        var token = await _tokens.IssueAsync(dose, admin.Id);

        await _tokens.RedeemAsync(token, DoseAction.Taken);

        (await _tokens.RedeemAsync(token, DoseAction.Taken)).Should().Be(DoseTokenResult.Success);
        QuantityOf(med.Id).Should().Be("21 таб."); // списано один раз
        (await _tokens.RedeemAsync(token, DoseAction.Skip)).Should().Be(DoseTokenResult.Conflict);
    }

    [Fact]
    public async Task Redeem_Snooze_SetsSnoozedUntil()
    {
        var (admin, _, dose, _) = SeedPendingDose();
        var token = await _tokens.IssueAsync(dose, admin.Id);

        (await _tokens.RedeemAsync(token, DoseAction.Snooze10)).Should().Be(DoseTokenResult.Success);

        var row = Db.MedicationDoses.AsNoTracking().Single();
        row.Status.Should().Be(DoseStatus.Snoozed);
        row.SnoozedUntil.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Redeem_Skip_MarksSkipped()
    {
        var (admin, _, dose, _) = SeedPendingDose();
        var token = await _tokens.IssueAsync(dose, admin.Id);

        (await _tokens.RedeemAsync(token, DoseAction.Skip)).Should().Be(DoseTokenResult.Success);

        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-such-token")]
    public async Task Redeem_UnknownToken_IsNotFound(string token) =>
        (await _tokens.RedeemAsync(token, DoseAction.Taken)).Should().Be(DoseTokenResult.NotFound);

    [Fact]
    public async Task Redeem_ExpiredToken_IsNotFound()
    {
        var (admin, _, dose, _) = SeedPendingDose();
        var token = await _tokens.IssueAsync(dose, admin.Id);
        Db.DoseActionTokens.Single().ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        Db.SaveChanges();

        (await _tokens.RedeemAsync(token, DoseAction.Taken)).Should().Be(DoseTokenResult.NotFound);
        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Pending);
    }

    [Fact]
    public async Task Redeem_TakenElsewhere_SnoozeConflicts_ButTakenStaysIdempotent()
    {
        var (admin, course, dose, _) = SeedPendingDose();
        var snoozeToken = await _tokens.IssueAsync(dose, admin.Id);
        var takenToken = await _tokens.IssueAsync(dose, admin.Id);
        await Doses.ApplyToDoseAsync(admin.Id, dose.Id, DoseAction.Taken); // отметили в приложении

        (await _tokens.RedeemAsync(snoozeToken, DoseAction.Snooze10)).Should().Be(DoseTokenResult.Conflict);
        (await _tokens.RedeemAsync(takenToken, DoseAction.Taken)).Should().Be(DoseTokenResult.Success);
        course.Should().NotBeNull();
    }

    [Fact]
    public async Task Redeem_RecipientLostAccess_IsNotFound()
    {
        var (family, admin) = SeedFamily();
        var helper = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id);
        var at = MinuteFromNow(-5);
        var course = SeedCourse(null, dependent.Id, family.Id, admin.Id, OnceAt(at), at.AddMinutes(-1));
        var dose = new MedicationDose
        {
            Id = Guid.NewGuid(), CourseId = course.Id, ScheduledAt = at, Units = 1, Status = DoseStatus.Pending, CreatedAt = at,
        };
        Db.MedicationDoses.Add(dose);
        Db.SaveChanges();
        var token = await _tokens.IssueAsync(dose, helper.Id);

        Db.FamilyMembers.Remove(Db.FamilyMembers.Single(m => m.UserId == helper.Id)); // человек вышел из семьи
        Db.SaveChanges();

        (await _tokens.RedeemAsync(token, DoseAction.Taken)).Should().Be(DoseTokenResult.NotFound);
        Db.MedicationDoses.AsNoTracking().Single().Status.Should().Be(DoseStatus.Pending);
    }

    // ── Payload push-уведомления ────────────────────────────────────────────

    [Fact]
    public async Task Customizer_BuildsGenericTextWithActionButtons_WithoutDrugName()
    {
        var (admin, course, dose, _) = SeedPendingDose();

        var c = await _customizer.BuildAsync(DueNotification(admin.Id, dose.Id));

        c.Should().NotBeNull();
        c!.Body.Should().StartWith("Время принять лекарство · ");
        c.Body.Should().NotContain(course.DrugName);
        c.Tag.Should().Be($"dose-{dose.Id}");
        c.Actions!.Select(a => a.Action).Should().Equal("taken", "snooze10", "snooze30", "skip");
        c.DefaultUrl.Should().Be($"/health/intake/dose/{dose.Id}");
        c.SendRequestUrls!.Keys.Should().BeEquivalentTo("taken", "snooze10", "snooze30", "skip");
        c.SendRequestUrls["taken"].Should().StartWith("/api/public/dose-actions/").And.EndWith("?a=taken");
        c.Silent.Should().BeFalse();
        Db.DoseActionTokens.Should().ContainSingle(); // один токен на уведомление, общий для всех кнопок
    }

    [Fact]
    public async Task Customizer_ButtonUrlsCarryWorkingToken()
    {
        var (admin, _, dose, _) = SeedPendingDose();
        var c = await _customizer.BuildAsync(DueNotification(admin.Id, dose.Id));
        var url = c!.SendRequestUrls!["taken"];
        var token = url["/api/public/dose-actions/".Length..url.IndexOf('?')];

        (await _tokens.RedeemAsync(token, DoseAction.Taken)).Should().Be(DoseTokenResult.Success);
    }

    [Fact]
    public async Task Customizer_QuietHours_MakeItSilent()
    {
        var (admin, _, dose, _) = SeedPendingDose();
        var user = Db.Users.Single(u => u.Id == admin.Id);
        var now = TimeOnly.FromDateTime(DateTime.UtcNow); // пояс пользователя — UTC
        user.QuietHoursFrom = now.AddHours(-1);
        user.QuietHoursTo = now.AddHours(1);
        Db.SaveChanges();

        var c = await _customizer.BuildAsync(DueNotification(admin.Id, dose.Id));

        c!.Silent.Should().BeTrue();
    }

    [Theory]
    [InlineData(DoseStatus.Taken)]
    [InlineData(DoseStatus.Skipped)]
    [InlineData(DoseStatus.Missed)]
    public async Task Customizer_ClosedDose_NoPush(DoseStatus status)
    {
        var (admin, _, dose, _) = SeedPendingDose();
        Db.MedicationDoses.Single().Status = status;
        Db.SaveChanges();

        (await _customizer.BuildAsync(DueNotification(admin.Id, dose.Id))).Should().BeNull();
        Db.DoseActionTokens.Should().BeEmpty();
    }

    [Fact]
    public void CanHandle_OnlyDoseDue()
    {
        _customizer.CanHandle(NotificationType.MedicationDoseDue).Should().BeTrue();
        _customizer.CanHandle(NotificationType.MedicationDoseMissed).Should().BeFalse();
        _customizer.CanHandle(NotificationType.MedicationExpiringSoon).Should().BeFalse();
    }

    [Fact]
    public void Payload_WithCustomization_HasNgswActionShape_AndDefaultPayloadIsUnchanged()
    {
        var custom = new PushCustomization(
            Body: "Время принять лекарство · 14:00", Tag: "dose-1", Renotify: true, RequireInteraction: true, Silent: true,
            Actions: [new PushActionButton("taken", "Принял")], DefaultUrl: "/health/intake/dose/1",
            SendRequestUrls: new Dictionary<string, string> { ["taken"] = "/api/public/dose-actions/T?a=taken" });

        using var withActions = JsonDocument.Parse(WebPushNotificationSender.BuildPayload(custom));
        var n = withActions.RootElement.GetProperty("notification");
        n.GetProperty("title").GetString().Should().Be("FamilyHub");
        n.GetProperty("body").GetString().Should().Be("Время принять лекарство · 14:00");
        n.GetProperty("tag").GetString().Should().Be("dose-1");
        n.GetProperty("silent").GetBoolean().Should().BeTrue();
        n.GetProperty("requireInteraction").GetBoolean().Should().BeTrue();
        n.GetProperty("actions")[0].GetProperty("action").GetString().Should().Be("taken");
        var click = n.GetProperty("data").GetProperty("onActionClick");
        click.GetProperty("default").GetProperty("operation").GetString().Should().Be("navigateLastFocusedOrOpen");
        click.GetProperty("default").GetProperty("url").GetString().Should().Be("/health/intake/dose/1");
        click.GetProperty("taken").GetProperty("operation").GetString().Should().Be("sendRequest");
        click.GetProperty("taken").GetProperty("url").GetString().Should().Be("/api/public/dose-actions/T?a=taken");

        using var plain = JsonDocument.Parse(WebPushNotificationSender.BuildPayload());
        var p = plain.RootElement.GetProperty("notification");
        p.GetProperty("body").GetString().Should().Be("Новое уведомление");
        p.TryGetProperty("actions", out _).Should().BeFalse();
        p.GetProperty("data").GetProperty("onActionClick").GetProperty("default").GetProperty("url").GetString().Should().Be("/notifications");
    }
}
