using SkiaSharp;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// Серверный аналог фронтового <c>shared/util/image-compression.ts</c> (canvas-ресайз перед
/// загрузкой) — нужен, потому что вложение до сервера может дойти и не через веб-клиент
/// (мобильное приложение, прямой вызов API), а контекст локальной vision-модели («ветка
/// medicalrecords») стоит беречь так же строго, как в MedicationOcrService.
/// HEIC: SkiaSharp на Linux его не декодирует (нет системного кодека), <see cref="Downscale"/>
/// в этом случае кидает <see cref="NotSupportedException"/> — вызывающий код (расширение
/// MedicalDocumentExtractionProcessor) ловит её и переводит задачу в Failed с понятным текстом.
/// Путь через UI этого не задевает: фронт всегда конвертирует HEIC в JPEG перед отправкой.
/// </summary>
public static class ImageDownscaler
{
    public static DecodedImage Downscale(byte[] source, int maxDimension, int jpegQuality)
    {
        using var original = SKBitmap.Decode(source)
            ?? throw new NotSupportedException("Не удалось декодировать изображение (неподдерживаемый формат).");

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
