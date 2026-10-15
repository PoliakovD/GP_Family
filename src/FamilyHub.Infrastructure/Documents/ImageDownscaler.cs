using BitMiracle.LibTiff.Classic;
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
/// которого нет на Linux. Для TIFF есть фолбэк — BitMiracle.LibTiff.NET: чисто управляемый порт
/// libtiff (без нативных musl-биндингов, лицензия BSD). Раньше фолбэком был SixLabors.ImageSharp,
/// но его 2.x получил непатченные High-уязвимости, а 4.x требует лицензионный ключ (2026-10-15).
/// Для HEIC фолбэка нет (декодирование требует HEVC-кодек, которого нет у SkiaSharp на Linux) —
/// путь через UI это не задевает: фронт всегда конвертирует HEIC в JPEG перед отправкой (см.
/// shared/util/image-compression.ts); прямая загрузка HEIC мимо веб-клиента (мобильное API)
/// остаётся Failed/Unsupported с понятной причиной вместо тихого отказа.
/// </summary>
public static class ImageDownscaler
{
    /// <summary>Потолок размера TIFF до декодирования: растр читается целиком в int[] (4 байта на
    /// пиксель), так что без потолка крошечный файл с заявленными огромными размерами — DoS по
    /// памяти. 40 Мп ≈ 160 МБ — с запасом больше любого скана A4 в 600 dpi (~35 Мп).</summary>
    private const long MaxTiffPixels = 40_000_000;

    static ImageDownscaler()
    {
        // По умолчанию LibTiff.Net пишет предупреждения и ошибки разбора в Console — для серверного
        // процесса это мусор в stdout; о неуспехе сообщают коды возврата, их и проверяем.
        Tiff.SetErrorHandler(new SilentTiffErrorHandler());
    }

    public static DecodedImage Downscale(byte[] source, int maxDimension, int jpegQuality)
    {
        SKBitmap? original;
        try
        {
            original = SKBitmap.Decode(source);
        }
        catch (ArgumentNullException)
        {
            // Наблюдаемая особенность SkiaSharp: для формата без кодека (TIFF всегда, HEIC без
            // системного кодека) SKCodec.Create возвращает null, а внутренняя перегрузка
            // SKBitmap.Decode(SKCodec) не проверяет это и бросает ArgumentNullException вместо
            // того, чтобы дать внешней SKBitmap.Decode(byte[]) вернуть null, как документировано.
            // Трактуем как обычный неуспех декодирования — тот же путь, что и настоящий null.
            original = null;
        }

        original ??= DecodeTiffFallback(source);

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
                    // Эквивалент прежнего SKFilterQuality.Medium (устарел в SkiaSharp 3).
                    resized = original.Resize(targetInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
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

    /// <summary>Покрывает TIFF (SkiaSharp его не декодирует вовсе) — первую страницу многостраничного.
    /// Для прочих форматов, которые не осилил SkiaSharp (практически — только HEIC), бросает
    /// NotSupportedException — вызывающий код (DocumentTextExtractor/AttachmentPreviewRenderer)
    /// ловит именно этот тип.
    ///
    /// Только TIFF по сигнатуре (аудит security-audit-2026-10, M7): фолбэк существует ради TIFF, и
    /// всё остальное, что не декодировал SkiaSharp (в том числе специально испорченные PNG/JPEG),
    /// до второго декодера не доходит.</summary>
    private static SKBitmap DecodeTiffFallback(byte[] source)
    {
        if (!IsTiff(source))
            throw new NotSupportedException("Не удалось декодировать изображение (неподдерживаемый формат).");

        using var tiff = Tiff.ClientOpen("attachment.tif", "r", new MemoryStream(source, writable: false), new TiffStream())
            ?? throw new NotSupportedException("Не удалось декодировать изображение (повреждённый TIFF).");

        var width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        var height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        if (width <= 0 || height <= 0)
            throw new NotSupportedException("Не удалось декодировать изображение (повреждённый TIFF).");
        if ((long)width * height > MaxTiffPixels)
            throw new NotSupportedException("Изображение слишком большое.");

        // ReadRGBAImageOriented приводит любую фотометрию/сжатие/битность к 8-битному RGBA.
        // Пиксель упакован как ABGR в int (R — младший байт), то есть в памяти little-endian это
        // ровно байты R, G, B, A — раскладка SKColorType.Rgba8888.
        var raster = new int[width * height];
        if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
            throw new NotSupportedException("Не удалось декодировать изображение (повреждённый TIFF).");

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(raster, 0, bitmap.GetPixels(), raster.Length);
        return bitmap;
    }

    /// <summary>Сигнатуры TIFF и BigTIFF: II*\0, MM\0*, II+\0, MM\0+.</summary>
    private static bool IsTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && ((data[0] == 'I' && data[1] == 'I' && data[3] == 0 && (data[2] == 42 || data[2] == 43))
            || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && (data[3] == 42 || data[3] == 43)));

    private sealed class SilentTiffErrorHandler : TiffErrorHandler
    {
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }
}
