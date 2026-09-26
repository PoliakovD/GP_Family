using System.Text;
using FamilyHub.Infrastructure.Documents;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Previews;

/// <summary>Типизированный HTTP-клиент на сайдкар Gotenberg (LibreOffice headless), тот же
/// приём, что LmStudioJsonClient/BraveSearchProvider — AddHttpClient&lt;TInterface, TImpl&gt;
/// с BaseAddress/Timeout из опций (см. Program.cs).</summary>
public class GotenbergConverter(HttpClient http, ILogger<GotenbergConverter> logger) : IGotenbergConverter
{
    /// <summary>Gotenberg выбирает конвертер LibreOffice по расширению в имени части формы, не по
    /// заголовку Content-Type — оригинальное имя файла для этого не годится (не ПДн ли там, и
    /// не факт, что расширение вообще совпадает с ContentType), поэтому подставляем нейтральное
    /// имя с расширением, выведенным из allow-list.</summary>
    private static readonly Dictionary<string, string> ExtensionsByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        [DocumentContentTypes.Docx] = "docx",
        [DocumentContentTypes.Xlsx] = "xlsx",
        [DocumentContentTypes.Xls] = "xls",
        [DocumentContentTypes.Doc] = "doc",
        [DocumentContentTypes.Rtf] = "rtf",
        [DocumentContentTypes.Html] = "html",
    };

    public async Task<byte[]?> ConvertToPdfAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        if (!ExtensionsByContentType.TryGetValue(contentType, out var extension))
            throw new NotSupportedException($"Gotenberg: конвертация {contentType} не поддержана.");

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(content);
        form.Add(fileContent, "files", $"document.{extension}");

        try
        {
            using var response = await http.PostAsync("/forms/libreoffice/convert", form, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "Gotenberg вернул {StatusCode} при конвертации {ContentType}: {Body}",
                    response.StatusCode, contentType, body);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Gotenberg недоступен при конвертации {ContentType}.", contentType);
            return null;
        }
    }

    /// <summary>Chromium-маршрут Gotenberg: страница обязана называться index.html, колонтитул — footer.html
    /// (шаблон получает классы pageNumber/totalPages от самого Chromium, JS не нужен — на сайдкаре он выключен).
    /// Поля страницы и формат передаются полями формы, а не CSS @page: колонтитул рисуется в поле страницы.</summary>
    public async Task<byte[]?> ConvertHtmlToPdfAsync(string html, string? footerHtml = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var page = new StringContent(html, Encoding.UTF8, "text/html");
        form.Add(page, "files", "index.html");
        using var footer = footerHtml is null ? null : new StringContent(footerHtml, Encoding.UTF8, "text/html");
        if (footer is not null) form.Add(footer, "files", "footer.html");

        // A4 в дюймах; поля — под колонтитул снизу.
        foreach (var (name, value) in new[]
        {
            ("paperWidth", "8.27"), ("paperHeight", "11.7"),
            ("marginTop", "0.55"), ("marginBottom", "0.75"), ("marginLeft", "0.6"), ("marginRight", "0.6"),
            ("printBackground", "true"),
        })
        {
            form.Add(new StringContent(value), name);
        }

        try
        {
            using var response = await http.PostAsync("/forms/chromium/convert/html", form, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Gotenberg вернул {StatusCode} при HTML→PDF: {Body}", response.StatusCode, body);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Gotenberg недоступен при HTML→PDF.");
            return null;
        }
    }
}
