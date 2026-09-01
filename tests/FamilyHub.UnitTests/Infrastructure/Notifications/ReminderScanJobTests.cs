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

public class ReminderScanJobTests : SqliteTestBase, IAsyncLifetime
{
    private readonly INotificationSender _sender = Substitute.For<INotificationSender>();
    private readonly DomainEventTestPipeline _pipeline;

    public ReminderScanJobTests()
    {
        _pipeline = new DomainEventTestPipeline(ConnectionString, TestFieldCipher, _sender);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _pipeline.DisposeAsync();

    /// <summary>Собственный NotificationSendingService на Db (не _pipeline.Notifications/_consumerDb) —
    /// в проде ReminderScanJob и его ретрай-свип делят один и тот же per-job scoped AppDbContext,
    /// а консьюмеры шины (в тесте — асинхронный MassTransit-харнесс поверх _consumerDb) физически
    /// в другом процессе, значит с ним никогда не конкурируют за один DbContext. В этом харнессе
    /// consumer уже стартует асинхронно сразу после PublishAsync (см. DomainEventTestPipeline) —
    /// если бы SendPendingAsync звал тот же singleton NotificationSendingService, что и consumer
    /// (оба на _consumerDb), запись справочника UserNotificationPreference внутри TrySendAsync
    /// гонялась бы с ещё не завершившимся хендлером за один DbContext (не потокобезопасен) —
    /// перемежающийся "A second operation was started on this context instance ...".</summary>
    private ReminderScanJob CreateSut(NotificationOptions? options = null) =>
        new(Db, _pipeline.Publisher, new NotificationSendingService(Db, [_sender], NullLogger<NotificationSendingService>.Instance),
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
    public async Task RunAsync_MemberBirthdayWithinWindow_NotifiesOtherActiveMembers_ButNotTheBirthdayPerson()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var birthdayMember = Db.AddMember(family.Id);
        var soon = DateTime.UtcNow.AddDays(3);
        birthdayMember.BirthDate = new DateOnly(soon.Year - 20, soon.Month, soon.Day);
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        var notifications = Db.Notifications.Where(n => n.Type == NotificationType.BirthdayUpcoming).ToList();
        notifications.Should().ContainSingle(n => n.UserId == admin.Id);
        notifications.Should().NotContain(n => n.UserId == birthdayMember.Id, "именинник не должен получать оповещение о своём же ДР");
    }

    [Fact]
    public async Task RunAsync_MemberWithoutBirthDate_IsIgnored()
    {
        // TestData.NewUser() (см. SeedFamilyWithAdmin/AddMember) уже задаёт BirthDate по
        // умолчанию — эта проверка явно его обнуляет, чтобы покрыть путь "профиль не заполнен".
        var (family, admin) = Db.SeedFamilyWithAdmin();
        admin.BirthDate = null;
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        Db.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_MemberInTwoFamilies_PublishesSeparateEventPerFamily()
    {
        var (familyA, personA) = Db.SeedFamilyWithAdmin("A");
        var (familyB, personB) = Db.SeedFamilyWithAdmin("B");
        var soon = DateTime.UtcNow.AddDays(3);
        personA.BirthDate = new DateOnly(soon.Year - 20, soon.Month, soon.Day);
        Db.FamilyMembers.Add(TestData.NewMember(familyB.Id, personA.Id)); // именинник — ещё и в семье B
        Db.FamilyMembers.Add(TestData.NewMember(familyA.Id, personB.Id)); // получатель — тоже в обеих семьях
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

        var notifications = Db.Notifications.Where(n => n.Type == NotificationType.BirthdayUpcoming).ToList();
        // personB (единственный получатель в каждой из двух семей, кроме самого именинника)
        // получает ДВА отдельных оповещения — по одному на каждую семью, где состоит именинник.
        notifications.Should().HaveCount(2);
        notifications.Should().OnlyContain(n => n.UserId == personB.Id);
        notifications.Select(n => n.FamilyId).Should().BeEquivalentTo([familyA.Id, familyB.Id]);
    }

    [Fact]
    public async Task RunAsync_DependentBirthdayWithinWindow_NotifiesActiveMembers()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var soon = DateTime.UtcNow.AddDays(3);
        Db.FamilyDependents.Add(new Domain.Entities.FamilyDependent
        {
            Id = Guid.NewGuid(), FamilyId = family.Id, FirstName = "Барсик", IsPet = true, Gender = Gender.Male,
            BirthDate = new DateOnly(soon.Year - 3, soon.Month, soon.Day), CreatedByUserId = admin.Id, CreatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        await RunAndDispatchAsync(CreateSut());

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

    // Регрессия на аудит module-review-2026-08-02/06-notifications-push-bot-outbox.md,
    // находка 3: раньше ретрай-свип не имел верхней границы давности — недоставленное оповещение
    // при затяжном сбое канала пыталось бы отправиться каждый день бесконечно.
    [Fact]
    public async Task RunAsync_PendingNotificationOlderThanRetryWindow_IsNotResent()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var stale = TestData.NewNotification(admin.Id, family.Id, "dk-stale-pending");
        stale.CreatedAt = DateTime.UtcNow.AddDays(-8); // старше дефолтного окна в 7 дней
        Db.Notifications.Add(stale);
        await Db.SaveChangesAsync();

        await CreateSut().RunAsync();

        await _sender.DidNotReceive().SendAsync(
            Arg.Is<Domain.Entities.Notification>(n => n.Id == stale.Id), Arg.Any<CancellationToken>());
        Db.Notifications.Single(n => n.Id == stale.Id).SentAt.Should().BeNull("устаревшая запись не должна помечаться отправленной — она просто исключена из свипа");
    }

    [Fact]
    public async Task RunAsync_PendingNotificationWithinRetryWindow_IsStillResent()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var fresh = TestData.NewNotification(admin.Id, family.Id, "dk-fresh-pending");
        fresh.CreatedAt = DateTime.UtcNow.AddDays(-6); // младше дефолтного окна в 7 дней
        Db.Notifications.Add(fresh);
        await Db.SaveChangesAsync();

        await CreateSut().RunAsync();

        await _sender.Received(1).SendAsync(
            Arg.Is<Domain.Entities.Notification>(n => n.Id == fresh.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CustomRetrySweepMaxAgeDays_IsRespected()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(admin.Id, family.Id, "dk-custom-window");
        notification.CreatedAt = DateTime.UtcNow.AddDays(-2);
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        await CreateSut(new NotificationOptions { RetrySweepMaxAgeDays = 1 }).RunAsync();

        await _sender.DidNotReceive().SendAsync(
            Arg.Is<Domain.Entities.Notification>(n => n.Id == notification.Id), Arg.Any<CancellationToken>());
    }
}
