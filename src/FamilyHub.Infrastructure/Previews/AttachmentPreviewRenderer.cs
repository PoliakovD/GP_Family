using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PDFtoImage;

namespace FamilyHub.Infrastructure.Previews;

/// <summary>
/// Диспетчер по ContentType — три исхода на вложение (Ready/Unsupported/Failed), не по одной
/// попытке на каждый вид артефакта. Переиспользует конвейер OCR (PdfPageRasterizer,
/// ImageDownscaler, см. .claude/patterns): рендер превью — тот же растровый путь, только с
/// другими (более щадящими) размером/DPI и без похода к vision-модели.
///
/// Модель артефактов — три вида (AttachmentPreviewKind), не «страница на каждый лист документа»:
/// PDF получает только миниатюру (вьюер рисует сам оригинал через pdf.js); Office сначала
/// конвертируется в PDF (Gotenberg/LibreOffice) и получает оба артефакта — Pdf для вьюера и
/// Thumbnail из его первой страницы; HEIC/TIFF получают нормализованный Page (то, что реально
/// умеет показать &lt;img&gt;) плюс Thumbnail; jpeg/png/webp — только Thumbnail, вьюер показывает
/// оригинал напрямую (браузер и так умеет).
/// </summary>
public class AttachmentPreviewRenderer(
    IGotenbergConverter gotenberg, IOptions<PreviewOptions> options, ILogger<AttachmentPreviewRenderer> logger)
{
    public async Task<PreviewRenderResult> RenderAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        if (!options.Value.Enabled)
            return PreviewRenderResult.NoArtifacts;

        try
        {
            if (contentType.Equals(DocumentContentTypes.Pdf, StringComparison.OrdinalIgnoreCase))
                return RenderPdf(content);

            if (IsOfficeRoute(contentType))
                return await RenderOfficeAsync(content, contentType, ct);

            if (DocumentContentTypes.Images.Contains(contentType))
                return RenderImage(content, contentType);

            // text/csv/xml/html(-как-не-office) и всё прочее — вьюер тянет исходник напрямую
            // (GET .../inline), артефакты превью не нужны.
            return PreviewRenderResult.NoArtifacts;
        }
        catch (NotSupportedException ex)
        {
            logger.LogInformation(ex, "Превью вложения: формат {ContentType} не поддержан рендерером.", contentType);
            return new PreviewRenderResult(PreviewRenderOutcome.Unsupported, [], ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось сгенерировать превью вложения ({ContentType}).", contentType);
            return new PreviewRenderResult(PreviewRenderOutcome.Failed, [], "Не удалось подготовить предпросмотр.");
        }
    }

    /// <summary>Office-документы, для которых у LibreOffice/Gotenberg есть конвертер в PDF — шире,
    /// чем DocumentContentTypes.Office (тот — только то, что распознаёт NPOI): Doc/Rtf/Html можно
    /// ПОКАЗАТЬ через LibreOffice, даже если извлечение текста для них не поддержано.</summary>
    private static bool IsOfficeRoute(string contentType) =>
        DocumentContentTypes.Office.Contains(contentType)
        || contentType.Equals(DocumentContentTypes.Doc, StringComparison.OrdinalIgnoreCase)
        || contentType.Equals(DocumentContentTypes.Rtf, StringComparison.OrdinalIgnoreCase)
        || contentType.Equals(DocumentContentTypes.Html, StringComparison.OrdinalIgnoreCase);

    private PreviewRenderResult RenderPdf(byte[] pdfBytes)
    {
        var pageCount = Conversion.GetPageCount(pdfBytes);
        if (pageCount <= 0)
            return new PreviewRenderResult(PreviewRenderOutcome.Unsupported, [], "PDF без страниц.");

        var pages = PdfPageRasterizer.RasterizePages(pdfBytes, options.Value.ThumbnailDpi, maxPages: 1);
        if (pages.Count == 0)
            return new PreviewRenderResult(PreviewRenderOutcome.Failed, [], "Не удалось отрисовать миниатюру PDF.");

        var thumb = ImageDownscaler.Downscale(pages[0], options.Value.ThumbnailMaxDimension, options.Value.JpegQuality);
        return new PreviewRenderResult(PreviewRenderOutcome.Ready,
            [new RenderedPreviewArtifact(AttachmentPreviewKind.Thumbnail, thumb.Bytes, thumb.ContentType, null, null, pageCount)], null);
    }

    private async Task<PreviewRenderResult> RenderOfficeAsync(byte[] bytes, string contentType, CancellationToken ct)
    {
        var pdfBytes = await gotenberg.ConvertToPdfAsync(bytes, contentType, ct);
        if (pdfBytes is null)
            return new PreviewRenderResult(PreviewRenderOutcome.Failed, [], "Не удалось сконвертировать документ в PDF.");

        var pdfResult = RenderPdf(pdfBytes);
        if (pdfResult.Outcome != PreviewRenderOutcome.Ready)
            return pdfResult;

        var pageCount = pdfResult.Artifacts[0].PageCount;
        var artifacts = new List<RenderedPreviewArtifact>(pdfResult.Artifacts)
        {
            new(AttachmentPreviewKind.Pdf, pdfBytes, DocumentContentTypes.Pdf, null, null, pageCount),
        };
        return new PreviewRenderResult(PreviewRenderOutcome.Ready, artifacts, null);
    }

    private PreviewRenderResult RenderImage(byte[] bytes, string contentType)
    {
        // heic/tiff: браузер сам их не отрисует — нормализуем в JPEG полного размера (Page).
        // jpeg/png/webp вьюер показывает через /inline напрямую, здесь нужна только миниатюра.
        var needsNormalizedPage =
            contentType.Equals(DocumentContentTypes.Heic, StringComparison.OrdinalIgnoreCase)
            || contentType.Equals(DocumentContentTypes.Tiff, StringComparison.OrdinalIgnoreCase);

        var thumb = ImageDownscaler.Downscale(bytes, options.Value.ThumbnailMaxDimension, options.Value.JpegQuality);
        var artifacts = new List<RenderedPreviewArtifact>
        {
            new(AttachmentPreviewKind.Thumbnail, thumb.Bytes, thumb.ContentType, null, null, null),
        };

        if (needsNormalizedPage)
        {
            var page = ImageDownscaler.Downscale(bytes, options.Value.PageMaxDimension, options.Value.JpegQuality);
            artifacts.Add(new RenderedPreviewArtifact(AttachmentPreviewKind.Page, page.Bytes, page.ContentType, null, null, null));
        }

        return new PreviewRenderResult(PreviewRenderOutcome.Ready, artifacts, null);
    }
}
