using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.MedicationCourses;
using FamilyHub.TestUtils;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FamilyHub.UnitTests.Modules.Medical;

public class MedicationReminderSettingsServiceTests : MedicationCourseTestBase
{
    [Fact]
    public async Task MyWatchers_ReplacesSet_OnlyFamilyMembersAllowed()
    {
        var (family, admin) = SeedFamily();
        var a = AddMemberUtc(family.Id);
        var b = AddMemberUtc(family.Id);
        var stranger = Db.AddUser();

        (await Reminders.SetMyWatchersAsync(admin.Id, new SetMyWatchersRequest([a.Id]))).Should().Be(ReminderSettingsResult.Success);
        Db.MedicationWatchers.Select(w => w.WatcherUserId).Should().Equal(a.Id);

        (await Reminders.SetMyWatchersAsync(admin.Id, new SetMyWatchersRequest([b.Id]))).Should().Be(ReminderSettingsResult.Success);
        Db.MedicationWatchers.AsNoTracking().Select(w => w.WatcherUserId).Should().Equal(b.Id);

        (await Reminders.SetMyWatchersAsync(admin.Id, new SetMyWatchersRequest([stranger.Id]))).Should().Be(ReminderSettingsResult.Invalid);
        (await Reminders.SetMyWatchersAsync(admin.Id, new SetMyWatchersRequest([]))).Should().Be(ReminderSettingsResult.Success);
        Db.MedicationWatchers.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ListsCandidates_Dependents_AndAdultsWhoChoseMe()
    {
        var (family, admin) = SeedFamily();
        var mama = AddMemberUtc(family.Id);
        mama.FirstName = "Люба";
        var dependent = AddDependent(family.Id, admin.Id, "Бабушка");
        SeedCourse(null, dependent.Id, family.Id, admin.Id, AsNeeded());
        Db.MedicationWatchers.Add(new MedicationWatcher
        {
            Id = Guid.NewGuid(), SubjectUserId = mama.Id, WatcherUserId = admin.Id, NotifyMissed = true, CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        var settings = await Reminders.GetAsync(admin.Id);

        settings.TimeZoneId.Should().Be("UTC");
        settings.MyWatchers.Should().ContainSingle(c => c.UserId == mama.Id && !c.Enabled);
        var grandma = settings.Watching.Single(w => w.Kind == "dependent");
        grandma.Name.Should().Be("Бабушка");
        grandma.CourseCount.Should().Be(1);
        grandma.IsWatching.Should().BeFalse();
        var adult = settings.Watching.Single(w => w.Kind == "user");
        adult.Name.Should().Be("Люба");
        adult.IsWatching.Should().BeTrue();
    }

    [Fact]
    public async Task WatchingDependent_TurnOnAndOff()
    {
        var (family, admin) = SeedFamily();
        var member = AddMemberUtc(family.Id);
        var dependent = AddDependent(family.Id, admin.Id);

        await Reminders.SetWatchingAsync(member.Id, "dependent", dependent.Id, new SetWatchingRequest(true, true));
        var row = Db.MedicationWatchers.AsNoTracking().Single(w => w.WatcherUserId == member.Id);
        row.ReceiveReminders.Should().BeTrue();

        await Reminders.SetWatchingAsync(member.Id, "dependent", dependent.Id, new SetWatchingRequest(true, false));
        Db.MedicationWatchers.AsNoTracking().Single(w => w.WatcherUserId == member.Id).ReceiveReminders.Should().BeFalse();

        await Reminders.SetWatchingAsync(member.Id, "dependent", dependent.Id, new SetWatchingRequest(false, false));
        Db.MedicationWatchers.AsNoTracking().Should().NotContain(w => w.WatcherUserId == member.Id);
    }

    [Fact]
    public async Task WatchingDependentOfAnotherFamily_IsNotFound()
    {
        var (_, admin) = SeedFamily();
        var (otherFamily, otherAdmin) = Db.SeedFamilyWithAdmin("Другая");
        var foreign = AddDependent(otherFamily.Id, otherAdmin.Id);

        (await Reminders.SetWatchingAsync(admin.Id, "dependent", foreign.Id, new SetWatchingRequest(true, true)))
            .Should().Be(ReminderSettingsResult.NotFound);
    }

    [Fact]
    public async Task WatchingAdult_CanOnlyMuteNotSelfEnroll()
    {
        var (family, admin) = SeedFamily();
        var mama = AddMemberUtc(family.Id);

        // Взрослый сам выбирает наблюдателей — записаться в них самому нельзя.
        (await Reminders.SetWatchingAsync(admin.Id, "user", mama.Id, new SetWatchingRequest(true, false)))
            .Should().Be(ReminderSettingsResult.NotFound);

        await Reminders.SetMyWatchersAsync(mama.Id, new SetMyWatchersRequest([admin.Id]));
        (await Reminders.SetWatchingAsync(admin.Id, "user", mama.Id, new SetWatchingRequest(false, false)))
            .Should().Be(ReminderSettingsResult.Success);
        Db.MedicationWatchers.AsNoTracking().Single().NotifyMissed.Should().BeFalse();
    }

    [Fact]
    public async Task QuietHours_BothOrNone_NotEqual()
    {
        var (_, admin) = SeedFamily();

        (await Reminders.SetQuietHoursAsync(admin.Id, new QuietHoursRequest(new TimeOnly(23, 0), new TimeOnly(7, 0))))
            .Should().Be(ReminderSettingsResult.Success);
        Db.Users.AsNoTracking().Single(u => u.Id == admin.Id).QuietHoursFrom.Should().Be(new TimeOnly(23, 0));

        (await Reminders.SetQuietHoursAsync(admin.Id, new QuietHoursRequest(new TimeOnly(23, 0), null))).Should().Be(ReminderSettingsResult.Invalid);
        (await Reminders.SetQuietHoursAsync(admin.Id, new QuietHoursRequest(new TimeOnly(7, 0), new TimeOnly(7, 0)))).Should().Be(ReminderSettingsResult.Invalid);

        (await Reminders.SetQuietHoursAsync(admin.Id, new QuietHoursRequest(null, null))).Should().Be(ReminderSettingsResult.Success);
        Db.Users.AsNoTracking().Single(u => u.Id == admin.Id).QuietHoursFrom.Should().BeNull();
    }
}

