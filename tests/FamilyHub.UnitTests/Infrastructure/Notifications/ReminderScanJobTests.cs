using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.TestUtils;
using FamilyHub.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Notifications;

public class ReminderScanJobTests : SqliteTestBase
{
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();
    private readonly OutboxTestPipeline _pipeline;

    public ReminderScanJobTests()
    {
        _pipeline = new OutboxTestPipeline(Db, _sender);
    }

    private ReminderScanJob CreateSut(NotificationOptions? options = null) =>
        new(Db, _pipeline.Writer, _pipeline.Notifications,
            Options.Create(options ?? new NotificationOptions()), NullLogger<ReminderScanJob>.Instance);

    /// <summary>Полный цикл «как в проде»: скан публикует события, диспетчер доставляет их хендлерам.</summary>
    private async Task RunAndDispatchAsync(ReminderScanJob sut)
    {
        await sut.RunAsync();
        await _pipeline.DispatchAsync();
    }

    [Fact]
    public async Task RunAsync_MedicationExpiringSoon_NotifiesOnlyActiveMembers()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var pending = Db.AddMember(family.Id, FamilyRole.Member, MemberStatus.PendingApproval);
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, admin.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5)));
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        var notifications = Db.Notifications.Where(n => n.Type == NotificationType.MedicationExpiringSoon).ToList();
        notifications.Should().ContainSingle(n => n.UserId == admin.Id);
        notifications.Should().NotContain(n => n.UserId == pending.Id);
    }

    [Fact]
    public async Task RunAsync_MedicationAlreadyExpired_UsesExpiredTypeAndDedupPrefix()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, admin.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1)));
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        var notification = Db.Notifications.Single(n => n.UserId == admin.Id);
        notification.Type.Should().Be(NotificationType.MedicationExpired);
        notification.DedupKey.Should().StartWith("med-expired:");
    }

    [Fact]
    public async Task RunAsync_CalledTwiceForSameMedication_DoesNotCreateDuplicateNotifications()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, admin.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5)));
        await Db.SaveChangesAsync();
        var sut = CreateSut();

        await RunAndDispatchAsync(sut);
        await RunAndDispatchAsync(sut);

        Db.Notifications.Count(n => n.UserId == admin.Id && n.Type == NotificationType.MedicationExpiringSoon).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_MedicationFarFromExpiry_DoesNotNotify()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var medkit = TestData.NewMedkit(family.Id, admin.Id);
        Db.Medkits.Add(medkit);
        Db.Medications.Add(TestData.NewMedication(medkit.Id, family.Id, admin.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(365)));
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        Db.Notifications.Any(n => n.UserId == admin.Id).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_BirthdayWithinWindow_NotifiesActiveMembers()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var soon = DateTime.UtcNow.AddDays(3);
        Db.Birthdays.Add(TestData.NewBirthday(family.Id, new DateOnly(soon.Year - 10, soon.Month, soon.Day)));
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        Db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id && n.Type == NotificationType.BirthdayUpcoming);
    }

    [Fact]
    public async Task RunAsync_LeapDayBirthday_InNonLeapTargetYear_DoesNotThrow_ClampsToFeb28()
    {
        // Регрессия: SafeDate должен переносить 29 февраля на 28-е в невисокосный год, а не
        // кидать ArgumentOutOfRangeException из DateOnly(year, 2, 29).
        var (family, admin) = Db.SeedFamilyWithAdmin();
        Db.Birthdays.Add(TestData.NewBirthday(family.Id, new DateOnly(1996, 2, 29)));
        await Db.SaveChangesAsync();
        // Широкое окно гарантирует, что следующее 29-февральское вхождение (в этом или
        // следующем году) попадёт в диапазон вне зависимости от текущей даты прогона теста.
        var sut = CreateSut(new NotificationOptions { BirthdayWarningDays = 400 });

        var act = async () => await RunAndDispatchAsync(sut);

        await act.Should().NotThrowAsync();
        Db.Notifications.Should().ContainSingle(n => n.UserId == admin.Id && n.Type == NotificationType.BirthdayUpcoming);
    }

    [Fact]
    public async Task RunAsync_SendsPendingNotificationsAndMarksSentAt()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(admin.Id, family.Id, "dk-pending");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        await CreateSut().RunAsync();

        await _sender.Received(1).SendAsync(Arg.Is<Domain.Entities.Notification>(n => n.Id == notification.Id), Arg.Any<CancellationToken>());
        Db.Notifications.Single(n => n.Id == notification.Id).SentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_AlreadySentNotification_IsNotResent()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(admin.Id, family.Id, "dk-already-sent");
        notification.SentAt = DateTime.UtcNow.AddDays(-1);
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        await CreateSut().RunAsync();

        await _sender.DidNotReceive().SendAsync(Arg.Is<Domain.Entities.Notification>(n => n.Id == notification.Id), Arg.Any<CancellationToken>());
    }
}
