using System.Text;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Email;
using FamilyHub.Infrastructure.Email.Templates;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace FamilyHub.UnitTests.Infrastructure.Email;

/// <summary>
/// Не проверка, а инструмент: рендерит все письма в файлы, чтобы вёрстку можно было открыть в
/// браузере. Нужен, потому что dev-цикл бэкенда — dotnet publish в Docker (Dockerfile.dev),
/// без dotnet watch и без volume-монтирования исходников, а сами шаблоны — embedded-ресурсы,
/// то есть правка .html на диске без пересборки ни на что не влияет в любом случае. Цикл здесь:
/// `dotnet test --filter EmailPreview` (пересобирает только Infrastructure + UnitTests, секунды)
/// → F5 в уже открытой вкладке браузера. Путь стабильный (без Guid), поэтому вкладка переживает
/// повторные прогоны. Ассерт внутри делает прогон заодно дымовым тестом рендера: незаполненный
/// плейсхолдер или отсутствующий ресурс валят его, а не только молча портят письмо в проде.
/// </summary>
public class EmailPreviewWriter(ITestOutputHelper output)
{
    private static readonly string PreviewDir =
        Path.Combine(Path.GetTempPath(), "familyhub-email-preview");

    [Fact]
    public void WriteAllTemplates()
    {
        Directory.CreateDirectory(PreviewDir);
        var renderer = new EmailTemplateRenderer(Options.Create(new EmailOptions
        {
            PublicSiteUrl = "https://gp-family.ru",
        }));

        var samples = new List<(string Name, string Html)>();
        foreach (var purpose in Enum.GetValues<EmailCodePurpose>())
        {
            var (_, copy) = EmailOtpService.CopyFor(purpose);
            samples.Add(($"otp-{purpose}", renderer.RenderCode(copy, "482915", 10)));
        }

        const string demoEmail = "demo@example.com";
        samples.Add(("temporary-password", renderer.RenderTemporaryPassword(
            TelegramBindingService.TemporaryPasswordCopy(demoEmail), demoEmail, "Kd7mQx4Ttb2z")));

        foreach (var (name, html) in samples)
        {
            html.Should().NotContain("{{", $"в шаблоне {name} остался незаполненный плейсхолдер");
            var path = Path.Combine(PreviewDir, $"{name}.html");
            File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            output.WriteLine(path);
        }
    }
}
