using PDFtoImage;
using SkiaSharp;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>Рендер страниц PDF-скана (без текстового слоя) в растр через PDFium — единственный
/// способ распознать бланк, который лаборатория выгрузила как отсканированный PDF, а не как
/// текстовый документ. Результат сразу проходит через <see cref="ImageDownscaler"/>, поэтому
/// dpi рендера может быть умеренным (см. <c>Extraction:RasterDpi</c>) — четкость важнее размера
/// на этом шаге, сжатие для vision-модели происходит отдельно.</summary>
public static class PdfPageRasterizer
{
    public static IReadOnlyList<byte[]> RasterizePages(byte[] pdfBytes, int dpi, int maxPages)
    {
        var pageCount = Conversion.GetPageCount(pdfBytes);
        var pagesToRender = Math.Min(pageCount, maxPages);
        if (pagesToRender <= 0) return [];

        var options = new RenderOptions(Dpi: dpi);
        var result = new List<byte[]>(pagesToRender);
        foreach (var bitmap in Conversion.ToImages(pdfBytes, ..pagesToRender, options: options))
        {
            using (bitmap)
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                result.Add(data.ToArray());
            }
        }
        return result;
    }
}
