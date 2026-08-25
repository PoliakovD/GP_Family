using FamilyHub.Infrastructure.Documents;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Documents;

/// <summary>
/// Смоук-тест нативных зависимостей конвейера извлечения (ветка medicalrecords, шаг 1 порядка
/// работ из плана): PDFium (рендер PDF-страницы) и SkiaSharp (декод/ресайз/JPEG-энкод) должны
/// реально отработать, а не просто скомпилироваться. На Windows это проверяет корректность API;
/// решающая проверка — тот же тест внутри Linux/Alpine-контейнера API (см. Dockerfile), где риск
/// несовместимости нативных бинарников (musl) реален. Тестовые PDF/JPEG генерируются самим
/// SkiaSharp — нет смысла хранить бинарные фикстуры ради одной страницы текста.
/// </summary>
public class DocumentPipelineSmokeTests
{
    [Fact]
    public void ImageDownscaler_Downscale_ProducesSmallerValidJpeg()
    {
        var source = CreateJpeg(width: 3000, height: 2000);

        var result = ImageDownscaler.Downscale(source, maxDimension: 800, jpegQuality: 78);

        result.ContentType.Should().Be(DocumentContentTypes.Jpeg);
        result.Bytes.Should().NotBeEmpty();
        using var decoded = SKBitmap.Decode(result.Bytes);
        decoded.Should().NotBeNull();
        Math.Max(decoded!.Width, decoded.Height).Should().BeLessOrEqualTo(800);
    }

    [Fact]
    public void ImageDownscaler_Downscale_SmallerThanMax_DoesNotUpscale()
    {
        var source = CreateJpeg(width: 200, height: 150);

        var result = ImageDownscaler.Downscale(source, maxDimension: 800, jpegQuality: 78);

        using var decoded = SKBitmap.Decode(result.Bytes);
        decoded!.Width.Should().Be(200);
        decoded.Height.Should().Be(150);
    }

    [Fact]
    public void PdfPageRasterizer_RasterizePages_RendersRequestedPageCount()
    {
        var pdf = CreateOnePagePdf();

        var pages = PdfPageRasterizer.RasterizePages(pdf, dpi: 100, maxPages: 5);

        pages.Should().HaveCount(1);
        using var decoded = SKBitmap.Decode(pages[0]);
        decoded.Should().NotBeNull();
    }

    [Fact]
    public void PdfDocumentReader_ExtractText_ReadsTextLayer()
    {
        var pdf = CreateOnePagePdf();
        var reader = new PdfDocumentReader(NullLogger<PdfDocumentReader>.Instance);

        var result = reader.ExtractText(pdf);

        result.Success.Should().BeTrue();
        result.PageCount.Should().Be(1);
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, TextSize = 24 };
        canvas.DrawText("Гемоглобин 118 г/л", 10, 40, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] CreateOnePagePdf()
    {
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            using var canvas = document.BeginPage(400, 300);
            using var paint = new SKPaint { Color = SKColors.Black, TextSize = 18 };
            canvas.DrawText("Гемоглобин 118 г/л (норма 130-160)", 20, 40, paint);
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }
}
