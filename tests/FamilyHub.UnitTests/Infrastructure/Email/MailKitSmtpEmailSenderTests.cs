using FamilyHub.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Email;

public class MailKitSmtpEmailSenderTests
{
    private readonly ISmtpTransport _transport = Substitute.For<ISmtpTransport>();

    private MailKitSmtpEmailSender CreateSut(params SmtpProviderOptions[] providers) =>
        new(_transport, Options.Create(new EmailOptions { Providers = [.. providers] }),
            NullLogger<MailKitSmtpEmailSender>.Instance);

    private static SmtpProviderOptions Provider(string name, int? dailyLimit = null) => new()
    {
        Name = name,
        Host = $"{name}.example.ru",
        From = "noreply@example.ru",
        DailyLimit = dailyLimit,
    };

    [Fact]
    public async Task Send_FirstProviderWorks_SecondIsNotTouched()
    {
        var sut = CreateSut(Provider("primary"), Provider("fallback"));

        await sut.SendAsync("to@example.com", "тема", "текст");

        await _transport.Received(1).SendAsync(
            Arg.Is<SmtpProviderOptions>(p => p.Name == "primary"), "to@example.com", "тема", "текст", Arg.Any<CancellationToken>());
        await _transport.DidNotReceive().SendAsync(
            Arg.Is<SmtpProviderOptions>(p => p.Name == "fallback"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_FirstProviderFails_FallsOverToSecond()
    {
        _transport.SendAsync(Arg.Is<SmtpProviderOptions>(p => p.Name == "primary"),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("соединение оборвано"));
        var sut = CreateSut(Provider("primary"), Provider("fallback"));

        await sut.SendAsync("to@example.com", "тема", "текст");

        await _transport.Received(1).SendAsync(
            Arg.Is<SmtpProviderOptions>(p => p.Name == "fallback"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_DailyLimitReached_SkipsProvider()
    {
        var sut = CreateSut(Provider("limited", dailyLimit: 2), Provider("fallback"));

        await sut.SendAsync("a@example.com", "s", "b");
        await sut.SendAsync("b@example.com", "s", "b");
        // Лимит primary исчерпан — третье письмо уходит через fallback без попытки primary.
        await sut.SendAsync("c@example.com", "s", "b");

        await _transport.Received(2).SendAsync(
            Arg.Is<SmtpProviderOptions>(p => p.Name == "limited"), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _transport.Received(1).SendAsync(
            Arg.Is<SmtpProviderOptions>(p => p.Name == "fallback"), "c@example.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_AllProvidersFail_ThrowsWithAggregatedCauses()
    {
        _transport.SendAsync(Arg.Any<SmtpProviderOptions>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("недоступен"));
        var sut = CreateSut(Provider("p1"), Provider("p2"));

        var act = async () => await sut.SendAsync("to@example.com", "s", "b");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.InnerException.Should().BeOfType<AggregateException>()
            .Which.InnerExceptions.Should().HaveCount(2);
    }
}
