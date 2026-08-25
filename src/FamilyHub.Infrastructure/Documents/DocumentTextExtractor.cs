using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Documents;

/// <inheritdoc cref="IDocumentTextExtractor"/>
public class DocumentTextExtractor(
    PdfDocumentReader pdfReader,
    OfficeDocumentReader officeReader,
    IOptions<ExtractionOptions> options,
    ILogger<DocumentTextExtractor> logger) : IDocumentTextExtractor
{
    public Task<DocumentContent> ExtractAsync(byte[] bytes, string contentType, CancellationToken ct = default)
    {
        if (DocumentContentTypes.Images.Contains(contentType))
            return Task.FromResult(ExtractImage(bytes, contentType));

        if (contentType == DocumentContentTypes.Pdf)
            return Task.FromResult(ExtractPdf(bytes));

        if (DocumentContentTypes.Office.Contains(contentType))
            return Task.FromResult(ExtractOffice(bytes, contentType));

        if (DocumentContentTypes.PlainTextLike.Contains(contentType))
            return Task.FromResult(ExtractPlainText(bytes, contentType));

        if (contentType == DocumentContentTypes.Doc)
            return Task.FromResult(DocumentContent.Unsupported(
                "Старый формат .doc не поддержан распознаванием. Пересохраните файл в .docx или сфотографируйте документ."));

        return Task.FromResult(DocumentContent.Unsupported($"Формат {contentType} не поддержан распознаванием."));
    }

    private DocumentContent ExtractImage(byte[] bytes, string contentType)
    {
        if (contentType == DocumentContentTypes.Heic)
        {
            // Путь через UI сюда не попадает — фронт конвертирует HEIC в JPEG перед загрузкой
            // (shared/util/image-compression.ts). Прямой вызов API мимо фронта — легитимный,
            // но нераспознаваемый случай.
            return DocumentContent.Unsupported(
                "HEIC не поддержан распознаванием на сервере. Загрузите фото в формате JPEG/PNG.");
        }

        try
        {
            var downscaled = ImageDownscaler.Downscale(bytes, options.Value.MaxImageDimension, options.Value.JpegQuality);
            return DocumentContent.FromImages([downscaled]);
        }
        catch (NotSupportedException ex)
        {
            logger.LogWarning(ex, "Не удалось подготовить изображение к распознаванию.");
            return DocumentContent.Unsupported(ex.Message);
        }
    }

    private DocumentContent ExtractPdf(byte[] bytes)
    {
        var textResult = pdfReader.ExtractText(bytes);
        if (!textResult.Success)
            return DocumentContent.Unsupported("Не удалось открыть PDF — файл повреждён или защищён паролем.");

        if (textResult.HasTextLayer)
            return DocumentContent.FromText(textResult.Text!);

        // Текстового слоя нет — это скан, рендерим страницы в картинки и сжимаем тем же путём,
        // что и обычное фото (см. план: "PDF-сканы — рендерить через PDFium сразу").
        try
        {
            var pages = PdfPageRasterizer.RasterizePages(bytes, options.Value.RasterDpi, options.Value.MaxPages);
            if (pages.Count == 0)
                return DocumentContent.Unsupported("PDF без текстового слоя и без страниц для рендера.");

            var downscaled = pages
                .Select(page => ImageDownscaler.Downscale(page, options.Value.MaxImageDimension, options.Value.JpegQuality))
                .ToList();
            return DocumentContent.FromImages(downscaled);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось отрендерить PDF-скан в изображения.");
            return DocumentContent.Unsupported("Не удалось отрендерить сканированный PDF.");
        }
    }

    private DocumentContent ExtractOffice(byte[] bytes, string contentType)
    {
        var text = officeReader.ExtractText(bytes, contentType);
        return text is null
            ? DocumentContent.Unsupported("Не удалось прочитать документ — файл повреждён или в неожиданном формате.")
            : DocumentContent.FromText(text);
    }

    private static DocumentContent ExtractPlainText(byte[] bytes, string contentType) =>
        DocumentContent.FromText(PlainTextReader.Decode(bytes, contentType));
}
