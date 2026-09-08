using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// Серверный аналог фронтового <c>shared/util/image-compression.ts</c> (canvas-ресайз перед
/// загрузкой) — нужен, потому что вложение до сервера может дойти и не через веб-клиент
/// (мобильное приложение, прямой вызов API), а контекст локальной vision-модели («ветка
/// medicalrecords») стоит беречь так же строго, как в MedicationOcrService. Тот же путь
/// используется и генерацией превью (AttachmentPreviewRenderer) для нормализации TIFF в JPEG.
///
/// SkiaSharp — основной декодер (быстрее, уже используется для JPEG/PNG/WEBP), но TIFF у него не
/// декодируется в принципе (нет такого кодека в ядре Skia ни на одной платформе — SKBitmap.Decode
/// тихо возвращает null, не бросает), а HEIC — только платформенно, через системный кодек,
/// которого нет на Linux. Для TIFF есть безопасный фолбэк — SixLabors.ImageSharp: чисто
/// управляемый декодер (Formats.Tiff — часть ядра пакета, без нативных musl-биндингов), уже
/// используется транзитивно через NPOI и проверен в этом же alpine-контейнере. Для HEIC такого
/// фолбэка нет (декодирование требует HEVC-кодек, которого нет ни у SkiaSharp на Linux, ни у
/// ImageSharp) — путь через UI это не задевает: фронт всегда конвертирует HEIC в JPEG перед
/// отправкой (см. shared/util/image-compression.ts); прямая загрузка HEIC мимо веб-клиента
/// (мобильное API) остаётся Failed/Unsupported с понятной причиной вместо тихого отказа.
/// </summary>
public static class ImageDownscaler
{
    public static DecodedImage Downscale(byte[] source, int maxDimension, int jpegQuality)
    {
        SKBitmap? original;
        try
        {
            original = SKBitmap.Decode(source);
        }
        catch (ArgumentNullException)
        {
            // Наблюдаемая особенность этой версии SkiaSharp: для формата без кодека (TIFF всегда,
            // HEIC без системного кодека) SKCodec.Create возвращает null, а внутренняя перегрузка
            // SKBitmap.Decode(SKCodec) не проверяет это и бросает ArgumentNullException вместо
            // того, чтобы дать внешней SKBitmap.Decode(byte[]) вернуть null, как документировано.
            // Трактуем как обычный неуспех декодирования — тот же путь, что и настоящий null.
            original = null;
        }

        if (original is null)
            return DownscaleWithImageSharpFallback(source, maxDimension, jpegQuality);

        using (original)
        {
            var longSide = Math.Max(original.Width, original.Height);
            SKBitmap? resized = null;
            try
            {
                var scaled = original;
                if (longSide > maxDimension)
                {
                    var scale = (double)maxDimension / longSide;
                    var targetInfo = new SKImageInfo(
                        Math.Max(1, (int)Math.Round(original.Width * scale)),
                        Math.Max(1, (int)Math.Round(original.Height * scale)));
                    resized = original.Resize(targetInfo, SKFilterQuality.Medium)
                        ?? throw new NotSupportedException("Не удалось изменить размер изображения.");
                    scaled = resized;
                }

                using var image = SKImage.FromBitmap(scaled);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
                return new DecodedImage(data.ToArray(), DocumentContentTypes.Jpeg);
            }
            finally
            {
                resized?.Dispose();
            }
        }
    }

    /// <summary>Покрывает TIFF (SkiaSharp его на Linux не декодирует вовсе). Для форматов, которые
    /// не декодирует и ImageSharp (практически — только HEIC), бросает тот же
    /// NotSupportedException, что раньше бросал только сам SkiaSharp-путь — вызывающий код
    /// (DocumentTextExtractor/AttachmentPreviewRenderer) уже ловит именно этот тип.</summary>
    private static DecodedImage DownscaleWithImageSharpFallback(byte[] source, int maxDimension, int jpegQuality)
    {
        Image<Rgba32> image;
        try
        {
            image = Image.Load<Rgba32>(source);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new NotSupportedException("Не удалось декодировать изображение (неподдерживаемый формат).", ex);
        }

        using (image)
        {
            var longSide = Math.Max(image.Width, image.Height);
            if (longSide > maxDimension)
            {
                var scale = (double)maxDimension / longSide;
                var targetWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
                image.Mutate(x => x.Resize(targetWidth, targetHeight));
            }

            using var encoded = new MemoryStream();
            image.Save(encoded, new JpegEncoder { Quality = jpegQuality });
            return new DecodedImage(encoded.ToArray(), DocumentContentTypes.Jpeg);
        }
    }
}
