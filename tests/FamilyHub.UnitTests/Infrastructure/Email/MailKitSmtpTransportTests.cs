using FamilyHub.Infrastructure.Email;
using FluentAssertions;
using MimeKit;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Email;

public class MailKitSmtpTransportTests
{
    private static SmtpProviderOptions Provider(string? fromDisplayName = "FamilyHub") => new()
    {
        Name = "primary",
        Host = "primary.example.ru",
        From = "noreply@example.ru",
        FromDisplayName = fromDisplayName ?? string.Empty,
    };

    [Fact]
    public void BuildMessage_WithHtml_ProducesMultipartAlternative()
    {
        var message = MailKitSmtpTransport.BuildMessage(
            Provider(), "to@example.com", "тема", new EmailBody("текст", "<p>html</p>"));

        message.Body.Should().BeOfType<MultipartAlternative>();
        message.TextBody.Should().Be("текст");
        message.HtmlBody.Should().Be("<p>html</p>");
    }

    [Fact]
    public void BuildMessage_WithoutHtml_ProducesPlainTextOnly()
    {
        // Регресс-тест: dev/тестовые конфиги (Html == null) должны слать байт-в-байт то же
        // одиночное text/plain, что и до вёрстки писем.
        var message = MailKitSmtpTransport.BuildMessage(
            Provider(), "to@example.com", "тема", new EmailBody("текст"));

        message.Body.Should().BeOfType<TextPart>();
        ((TextPart)message.Body).Text.Should().Be("текст");
    }

    [Fact]
    public void BuildMessage_FromWithoutName_UsesFromDisplayName()
    {
        var message = MailKitSmtpTransport.BuildMessage(
            Provider("FamilyHub"), "to@example.com", "тема", new EmailBody("текст"));

        var from = (MailboxAddress)message.From[0];
        from.Name.Should().Be("FamilyHub");
        from.Address.Should().Be("noreply@example.ru");
    }

    [Fact]
    public void BuildMessage_FromAlreadyHasName_DoesNotOverrideWithFromDisplayName()
    {
        var provider = Provider("SomeDisplayName");
        provider.From = "\"Explicit Name\" <noreply@example.ru>";

        var message = MailKitSmtpTransport.BuildMessage(provider, "to@example.com", "тема", new EmailBody("текст"));

        var from = (MailboxAddress)message.From[0];
        from.Name.Should().Be("Explicit Name");
    }
}
