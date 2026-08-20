using FamilyHub.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Email;

/// <summary>
/// Добавлено 2026-08-19 вместе с YandexPostboxApiEmailSender: тот же failover-паттерн, что
/// MailKitSmtpEmailSenderTests проверяет между SMTP-провайдерами, но уровнем выше — между
/// разными IEmailSender-каналами (HTTPS API / SMTP).
/// </summary>
public class CompositeEmailSenderTests
{
    private static CompositeEmailSender CreateSut(params IEmailSender[] channels) =>
        new(channels, NullLogger<CompositeEmailSender>.Instance);

    [Fact]
    public async Task Send_FirstChannelWorks_SecondIsNotTouched()
    {
        var first = Substitute.For<IEmailSender>();
        var second = Substitute.For<IEmailSender>();
        var sut = CreateSut(first, second);

        await sut.SendAsync("to@example.com", "тема", new EmailBody("текст"));

        await first.Received(1).SendAsync("to@example.com", "тема", Arg.Is<EmailBody>(b => b.Text == "текст"), Arg.Any<CancellationToken>());
        await second.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Send_FirstChannelFails_FallsOverToSecond()
    {
        var first = Substitute.For<IEmailSender>();
        first.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("connect timeout"));
        var second = Substitute.For<IEmailSender>();
        var sut = CreateSut(first, second);

        await sut.SendAsync("to@example.com", "тема", new EmailBody("текст"));

        await second.Received(1).SendAsync("to@example.com", "тема", Arg.Any<EmailBody>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_AllChannelsFail_ThrowsWithAggregatedCauses()
    {
        var first = Substitute.For<IEmailSender>();
        var second = Substitute.For<IEmailSender>();
        first.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("connect timeout"));
        second.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("api error"));
        var sut = CreateSut(first, second);

        var act = async () => await sut.SendAsync("to@example.com", "s", new EmailBody("b"));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.InnerException.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Send_NoChannels_ThrowsImmediately()
    {
        var sut = CreateSut();

        var act = async () => await sut.SendAsync("to@example.com", "s", new EmailBody("b"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
