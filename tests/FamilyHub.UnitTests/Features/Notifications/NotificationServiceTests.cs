using FamilyHub.Api.Features.Notifications;
using FamilyHub.TestUtils;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Features.Notifications;

public class NotificationServiceTests : SqliteTestBase
{
    private readonly NotificationService _sut;

    public NotificationServiceTests()
    {
        _sut = new NotificationService(Db);
    }

    [Fact]
    public async Task GetMyNotificationsAsync_OnlyOwnNotifications()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var other = Db.AddMember(family.Id);
        Db.Notifications.Add(TestData.NewNotification(admin.Id, family.Id, "dk-1"));
        Db.Notifications.Add(TestData.NewNotification(other.Id, family.Id, "dk-2"));
        await Db.SaveChangesAsync();

        var result = await _sut.GetMyNotificationsAsync(admin.Id, unreadOnly: false);

        result.Should().ContainSingle();
    }

    [Fact]
    public async Task GetMyNotificationsAsync_UnreadOnly_FiltersOutRead()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var unread = TestData.NewNotification(admin.Id, family.Id, "dk-unread");
        var read = TestData.NewNotification(admin.Id, family.Id, "dk-read");
        read.IsRead = true;
        read.ReadAt = DateTime.UtcNow;
        Db.Notifications.AddRange(unread, read);
        await Db.SaveChangesAsync();

        var result = await _sut.GetMyNotificationsAsync(admin.Id, unreadOnly: true);

        result.Should().ContainSingle(n => n.Id == unread.Id);
    }

    [Fact]
    public async Task GetMyNotificationsAsync_OrderedByCreatedAtDescending()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var older = TestData.NewNotification(admin.Id, family.Id, "dk-older");
        older.CreatedAt = DateTime.UtcNow.AddHours(-2);
        var newer = TestData.NewNotification(admin.Id, family.Id, "dk-newer");
        newer.CreatedAt = DateTime.UtcNow;
        Db.Notifications.AddRange(older, newer);
        await Db.SaveChangesAsync();

        var result = await _sut.GetMyNotificationsAsync(admin.Id, unreadOnly: false);

        result.Select(n => n.Id).Should().Equal(newer.Id, older.Id);
    }

    [Fact]
    public async Task MarkReadAsync_OwnNotification_SetsReadAndReadAt()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(admin.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        var result = await _sut.MarkReadAsync(notification.Id, admin.Id);

        result.Should().Be(MarkReadResult.Success);
        var updated = Db.Notifications.Single(n => n.Id == notification.Id);
        updated.IsRead.Should().BeTrue();
        updated.ReadAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkReadAsync_OtherUsersNotification_ReturnsNotFound()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var other = Db.AddMember(family.Id);
        var notification = TestData.NewNotification(other.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();

        var result = await _sut.MarkReadAsync(notification.Id, admin.Id);

        result.Should().Be(MarkReadResult.NotFound);
        Db.Notifications.Single(n => n.Id == notification.Id).IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task MarkReadAsync_UnknownId_ReturnsNotFound()
    {
        var result = await _sut.MarkReadAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().Be(MarkReadResult.NotFound);
    }

    [Fact]
    public async Task MarkReadAsync_AlreadyRead_IsIdempotent_KeepsOriginalReadAt()
    {
        var (family, admin) = Db.SeedFamilyWithAdmin();
        var notification = TestData.NewNotification(admin.Id, family.Id, "dk-1");
        Db.Notifications.Add(notification);
        await Db.SaveChangesAsync();
        await _sut.MarkReadAsync(notification.Id, admin.Id);
        var firstReadAt = Db.Notifications.Single(n => n.Id == notification.Id).ReadAt;

        var result = await _sut.MarkReadAsync(notification.Id, admin.Id);

        result.Should().Be(MarkReadResult.Success);
        Db.Notifications.Single(n => n.Id == notification.Id).ReadAt.Should().Be(firstReadAt);
    }
}
