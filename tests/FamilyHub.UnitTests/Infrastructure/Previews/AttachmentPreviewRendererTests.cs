using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Previews;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Previews;

/// <summary>
/// Диспетчер AttachmentPreviewRenderer по ContentType — три исхода (Ready/Unsupported/Failed),
/// не по одной попытке на вид артефакта (см. докстринг класса). Фикстуры генерируются на лету
/// тем же приёмом, что DocumentPipelineSmokeTests — нет смысла хранить бинарные файлы ради теста.
/// </summary>
public class AttachmentPreviewRendererTests
{
    private static AttachmentPreviewRenderer CreateSut(IGotenbergConverter? gotenberg = null) => new(
        gotenberg ?? new NullGotenbergConverter(NullLogger<NullGotenbergConverter>.Instance),
        Options.Create(new PreviewOptions()),
        NullLogger<AttachmentPreviewRenderer>.Instance);

    [Fact]
    public async Task RenderAsync_Pdf_ProducesThumbnailWithPageCount()
    {
        var sut = CreateSut();
        var pdf = CreateOnePagePdf();

        var result = await sut.RenderAsync(pdf, DocumentContentTypes.Pdf);

        result.Outcome.Should().Be(PreviewRenderOutcome.Ready);
        result.Artifacts.Should().ContainSingle(a => a.Kind == AttachmentPreviewKind.Thumbnail);
        result.Artifacts.Single().PageCount.Should().Be(1);
    }

    [Fact]
    public async Task RenderAsync_Jpeg_ProducesOnlyThumbnail_NoNormalizedPage()
    {
        // jpeg/png/webp — браузер отрисует оригинал сам через /inline, лишний артефакт не нужен.
        var sut = CreateSut();
        var jpeg = CreateJpeg(1200, 900);

        var result = await sut.RenderAsync(jpeg, DocumentContentTypes.Jpeg);

        result.Outcome.Should().Be(PreviewRenderOutcome.Ready);
        result.Artifacts.Should().ContainSingle();
        result.Artifacts[0].Kind.Should().Be(AttachmentPreviewKind.Thumbnail);
    }

    [Fact]
    public async Task RenderAsync_Tiff_ProducesThumbnailAndNormalizedPage()
    {
        // TIFF — браузер сам не отрисует, нужна нормализованная JPEG-страница (см. ImageDownscaler
        // фолбэк на ImageSharp) в дополнение к миниатюре.
        var sut = CreateSut();
        var tiff = CreateTiff(600, 400);

        var result = await sut.RenderAsync(tiff, DocumentContentTypes.Tiff);

        result.Outcome.Should().Be(PreviewRenderOutcome.Ready);
        result.Artifacts.Should().HaveCount(2);
        result.Artifacts.Should().Contain(a => a.Kind == AttachmentPreviewKind.Thumbnail);
        result.Artifacts.Should().Contain(a => a.Kind == AttachmentPreviewKind.Page && a.ContentType == DocumentContentTypes.Jpeg);
    }

    [Fact]
    public async Task RenderAsync_Docx_GotenbergSucceeds_ProducesPdfAndThumbnail()
    {
        var pdfFromGotenberg = CreateOnePagePdf();
        var sut = CreateSut(new FakeGotenbergConverter(pdfFromGotenberg));

        var result = await sut.RenderAsync([1, 2, 3], DocumentContentTypes.Docx);

        result.Outcome.Should().Be(PreviewRenderOutcome.Ready);
        result.Artifacts.Should().Contain(a => a.Kind == AttachmentPreviewKind.Pdf && a.Bytes == pdfFromGotenberg);
        result.Artifacts.Should().Contain(a => a.Kind == AttachmentPreviewKind.Thumbnail);
    }

    [Fact]
    public async Task RenderAsync_Docx_GotenbergUnavailable_ReturnsFailed()
    {
        // NullGotenbergConverter (сайдкар не настроен) — то же самое, что реальный Gotenberg упал:
        // Failed, не исключение, вложение деградирует в карточку «Скачать».
        var sut = CreateSut();

        var result = await sut.RenderAsync([1, 2, 3], DocumentContentTypes.Docx);

        result.Outcome.Should().Be(PreviewRenderOutcome.Failed);
        result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderAsync_PlainText_NoArtifactsNeeded()
    {
        // txt/csv/xml — вьюер тянет исходник напрямую, генерировать нечего.
        var sut = CreateSut();

        var result = await sut.RenderAsync("hello"u8.ToArray(), DocumentContentTypes.PlainText);

        result.Outcome.Should().Be(PreviewRenderOutcome.Unsupported);
        result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderAsync_Heic_NoDecoderAvailable_ReturnsUnsupported()
    {
        // Ни SkiaSharp (на Linux), ни ImageSharp не декодируют HEIC — тестовые "HEIC-байты" здесь
        // это вообще не валидное изображение, что и воспроизводит тот же NotSupportedException.
        var sut = CreateSut();

        var result = await sut.RenderAsync([0x00, 0x01, 0x02, 0x03], DocumentContentTypes.Heic);

        result.Outcome.Should().Be(PreviewRenderOutcome.Unsupported);
        result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderAsync_Disabled_ReturnsUnsupportedWithoutCallingGotenberg()
    {
        var gotenberg = new FakeGotenbergConverter(CreateOnePagePdf());
        var sut = new AttachmentPreviewRenderer(
            gotenberg, Options.Create(new PreviewOptions { Enabled = false }), NullLogger<AttachmentPreviewRenderer>.Instance);

        var result = await sut.RenderAsync(CreateOnePagePdf(), DocumentContentTypes.Pdf);

        result.Outcome.Should().Be(PreviewRenderOutcome.Unsupported);
        gotenberg.CallCount.Should().Be(0);
    }

    private sealed class FakeGotenbergConverter(byte[]? pdfBytes) : IGotenbergConverter
    {
        public int CallCount { get; private set; }

        public Task<byte[]?> ConvertToPdfAsync(byte[] content, string contentType, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(pdfBytes);
        }

        public Task<byte[]?> ConvertHtmlToPdfAsync(string html, string? footerHtml = null, CancellationToken ct = default) =>
            Task.FromResult(pdfBytes);
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] CreateTiff(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new TiffEncoder());
        return stream.ToArray();
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
