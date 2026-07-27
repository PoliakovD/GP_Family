using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Email.Templates;

/// <summary>Текстовые слоты общей рамки письма (layout.html).</summary>
public sealed record EmailLayoutCopy(string Title, string Preheader, string Intro, string Footnote);

/// <summary>
/// Рендер HTML-части писем из embedded-шаблонов (Email\Templates\*.html). Движка шаблонов нет
/// намеренно: подстановок шесть, лишняя NuGet-зависимость в почтовом тракте не окупается.
/// Конкретный класс без интерфейса — тестового шва не нужно, все проверки идут через
/// публичные Render*-методы напрямую.
///
/// ВАЖНО: шаблоны — embedded-ресурсы, пекутся на этапе компиляции. Правка .html на диске без
/// пересборки ни на что не влияет — это не баг кэша ниже, а свойство embedded-ресурсов.
/// </summary>
public sealed class EmailTemplateRenderer(IOptions<EmailOptions> options)
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public string RenderCode(EmailLayoutCopy copy, string code, int ttlMinutes)
    {
        var block = Render("code-block.html", new Dictionary<string, string>
        {
            ["Code"] = Escape(code),
            ["TtlMinutes"] = ttlMinutes.ToString(CultureInfo.InvariantCulture),
        });
        return RenderLayout(copy, block);
    }

    public string RenderTemporaryPassword(EmailLayoutCopy copy, string email, string password)
    {
        var block = Render("password-block.html", new Dictionary<string, string>
        {
            ["Password"] = Escape(password),
            ["Email"] = Escape(email),
        });
        return RenderLayout(copy, block);
    }

    private string RenderLayout(EmailLayoutCopy copy, string contentBlockHtml) =>
        Render("layout.html", new Dictionary<string, string>
        {
            ["Title"] = Escape(copy.Title),
            ["Preheader"] = Escape(copy.Preheader),
            ["Intro"] = Escape(copy.Intro),
            ["Footnote"] = Escape(copy.Footnote),
            ["SiteUrl"] = Escape(options.Value.PublicSiteUrl),
            // Уже отрендерен и экранирован на предыдущем шаге — повторно НЕ экранируем.
            ["ContentBlock"] = contentBlockHtml,
        });

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    // Один проход MatchEvaluator, а не цепочка string.Replace: (1) подставленное значение не
    // пересканируется, поэтому значение, содержащее "{{SiteUrl}}", не может подменить ссылку
    // (HtmlEncode фигурные скобки не трогает); (2) незаполненный плейсхолдер — исключение при
    // рендере (и падение теста/сборки), а не молчаливое "{{Foo}}" в письме у пользователя.
    private static string Render(string resourceName, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(Load(resourceName), match =>
            values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new InvalidOperationException(
                    $"Шаблон {resourceName}: нет значения для плейсхолдера {{{{{match.Groups[1].Value}}}}}."));

    // Embedded-ресурсы неизменны в рамках сборки — кэш на весь процесс безопасен и избавляет
    // от чтения потока на каждое письмо.
    private static string Load(string resourceName) => Cache.GetOrAdd(resourceName, static name =>
    {
        var fullName = $"FamilyHub.Infrastructure.Email.Templates.{name}";
        using var stream = typeof(EmailTemplateRenderer).Assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Не найден embedded-ресурс {fullName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
