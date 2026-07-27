using FamilyHub.Api.Features.Auth;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Email;

public class EmailTemplateRendererTests
{
    private const string SiteUrl = "https://test.familyhub.local";

    private static EmailTemplateRenderer CreateSut() =>
        new(Options.Create(new EmailOptions { PublicSiteUrl = SiteUrl }));

    [Theory]
    [InlineData(EmailCodePurpose.Register)]
    [InlineData(EmailCodePurpose.LinkEmail)]
    [InlineData(EmailCodePurpose.ResetPassword)]
    [InlineData(EmailCodePurpose.TelegramBind)]
    public void RenderCode_AllPurposes_ProduceCompleteHtml(EmailCodePurpose purpose)
    {
        var sut = CreateSut();
        var (_, copy) = EmailOtpService.CopyFor(purpose);

        var html = sut.RenderCode(copy, "482915", 10);

        html.Should().NotContain("{{", "в шаблоне не должно оставаться незаполненных плейсхолдеров");
        html.Should().Contain("482915");
        html.Should().Contain(SiteUrl);
        html.Should().Contain(copy.Title);
    }

    [Fact]
    public void RenderTemporaryPassword_EscapesEmail()
    {
        var sut = CreateSut();
        var copy = TelegramBindingService.TemporaryPasswordCopy("a<b>&c@example.com");

        var html = sut.RenderTemporaryPassword(copy, "a<b>&c@example.com", "Kd7mQx4Ttb2z");

        html.Should().NotContain("<b>", "email должен быть HTML-экранирован, а не вставлен как есть");
        html.Should().Contain("&lt;b&gt;");
        html.Should().Contain("Kd7mQx4Ttb2z");
    }

    [Fact]
    public void Render_MissingPlaceholderValue_Throws()
    {
        // Незаполненный плейсхолдер должен ронять рендер (и тем самым сборку/тест), а не
        // молча оставлять "{{Foo}}" в письме у пользователя — см. EmailTemplateRenderer.Render.
        // Приватный статический метод достаём через рефлексию: это единственный способ
        // проверить fail-fast контракт напрямую, не заводя отдельный "битый" embedded-шаблон
        // только ради теста.
        var render = typeof(EmailTemplateRenderer).GetMethod(
            "Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var act = () => render.Invoke(null, ["code-block.html", new Dictionary<string, string>()]);

        // code-block.html подставляет {{Code}} раньше {{TtlMinutes}} (сверху вниз) — первым
        // и бросит именно он.
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Code*");
    }
}
