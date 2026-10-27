using FamilyHub.Infrastructure.Auth;
using FamilyHub.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Auth;

public class HubTicketServiceTests
{
    private static HubTicketService Service(string key, params string[] previous) =>
        new(Options.Create(new AttachmentDownloadOptions { DownloadSigningKey = key, PreviousSigningKeys = [.. previous] }));

    [Fact]
    public void Create_ThenValidate_ReturnsSameUser()
    {
        var sut = Service("key-a");
        var userId = Guid.NewGuid();

        var (ticket, expiresAt) = sut.Create(userId);

        sut.Validate(ticket).Should().Be(userId);
        expiresAt.Should().BeCloseTo(DateTime.UtcNow.Add(HubTicketService.Ttl), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Validate_RejectsTamperedUserOrSignature()
    {
        var sut = Service("key-a");
        var (ticket, _) = sut.Create(Guid.NewGuid());
        var parts = ticket.Split('.');

        sut.Validate($"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}").Should().BeNull("подпись привязана к пользователю");
        sut.Validate($"{parts[0]}.{long.Parse(parts[1]) + 3600}.{parts[2]}").Should().BeNull("срок нельзя продлить");
        sut.Validate($"{parts[0]}.{parts[1]}.{new string('0', parts[2].Length)}").Should().BeNull();
        sut.Validate("garbage").Should().BeNull();
        sut.Validate(null).Should().BeNull();
    }

    [Fact]
    public void Validate_RejectsExpiredTicket()
    {
        var sut = Service("key-a");
        var (ticket, _) = sut.Create(Guid.NewGuid());
        var parts = ticket.Split('.');

        // Срок проверяется раньше подписи — просроченный тикет отвергается, не доходя до HMAC.
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var forged = $"{parts[0]}.{past}.{parts[2]}";
        sut.Validate(forged).Should().BeNull();
    }

    [Fact]
    public void Validate_AcceptsTicketSignedByRetiredKey_AndRejectsUnknownKey()
    {
        var userId = Guid.NewGuid();
        var (oldTicket, _) = Service("old-key").Create(userId);

        Service("new-key", "old-key").Validate(oldTicket).Should().Be(userId, "ротация ключа не рвёт уже выданные тикеты");
        Service("new-key").Validate(oldTicket).Should().BeNull();
    }

    [Fact]
    public void Ticket_IsNotInterchangeableWithDownloadLinkSignature()
    {
        // Одна связка ключей с подписями ссылок на вложения — но разный payload (префикс hub:).
        var options = Options.Create(new AttachmentDownloadOptions { DownloadSigningKey = "shared" });
        var userId = Guid.NewGuid();
        var (ticket, _) = new HubTicketService(options).Create(userId);
        var parts = ticket.Split('.');

        new DownloadTokenService(options).Validate(userId, DownloadScope.File, long.Parse(parts[1]), parts[2])
            .Should().BeFalse();
    }
}
