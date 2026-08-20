using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using FamilyHub.Infrastructure.Email;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Email;

/// <summary>
/// Добавлено 2026-08-19: HTTPS API Yandex Postbox (SESv2-совместимый) как обход блокировки
/// исходящих SMTP-портов 587/465 у части провайдеров связи. IAmazonSimpleEmailServiceV2 —
/// мок, реальный HTTPS-вызов сюда не входит (аналог MailKitSmtpEmailSenderTests с моком
/// ISmtpTransport — там реальный SMTP-коннект тоже не тестируется юнит-тестом).
/// </summary>
public class YandexPostboxApiEmailSenderTests
{
    private readonly IAmazonSimpleEmailServiceV2 _client = Substitute.For<IAmazonSimpleEmailServiceV2>();

    private YandexPostboxApiEmailSender CreateSut(YandexPostboxApiOptions? postbox = null) =>
        new(_client, Options.Create(new EmailOptions
        {
            PostboxApi = postbox ?? new YandexPostboxApiOptions
            {
                AccessKeyId = "AKIDEXAMPLE",
                SecretAccessKey = "secret",
                From = "noreply@example.ru",
                FromDisplayName = "FamilyHub",
            },
        }));

    [Fact]
    public async Task SendAsync_BuildsRequest_WithFromDisplayNameAndTextAndHtmlBody()
    {
        _client.SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse());
        var sut = CreateSut();

        await sut.SendAsync("to@example.com", "тема", new EmailBody("текст", "<p>html</p>"));

        await _client.Received(1).SendEmailAsync(
            Arg.Is<SendEmailRequest>(r =>
                r.FromEmailAddress == "FamilyHub <noreply@example.ru>"
                && r.Destination.ToAddresses.Single() == "to@example.com"
                && r.Content.Simple.Subject.Data == "тема"
                && r.Content.Simple.Body.Text.Data == "текст"
                && r.Content.Simple.Body.Html!.Data == "<p>html</p>"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_TextOnlyBody_HtmlPartIsNull()
    {
        _client.SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse());
        var sut = CreateSut();

        await sut.SendAsync("to@example.com", "тема", new EmailBody("только текст"));

        await _client.Received(1).SendEmailAsync(
            Arg.Is<SendEmailRequest>(r => r.Content.Simple.Body.Html == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_NoFromDisplayName_UsesBareAddress()
    {
        _client.SendEmailAsync(Arg.Any<SendEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendEmailResponse());
        var sut = CreateSut(new YandexPostboxApiOptions
        {
            AccessKeyId = "AKIDEXAMPLE", SecretAccessKey = "secret", From = "noreply@example.ru", FromDisplayName = "",
        });

        await sut.SendAsync("to@example.com", "s", new EmailBody("b"));

        await _client.Received(1).SendEmailAsync(
            Arg.Is<SendEmailRequest>(r => r.FromEmailAddress == "noreply@example.ru"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_PostboxApiNotConfigured_Throws()
    {
        var sut = new YandexPostboxApiEmailSender(_client, Options.Create(new EmailOptions { PostboxApi = null }));

        var act = async () => await sut.SendAsync("to@example.com", "s", new EmailBody("b"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
