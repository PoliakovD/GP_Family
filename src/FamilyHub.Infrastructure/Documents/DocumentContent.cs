namespace FamilyHub.Infrastructure.Documents;

/// <summary>Один результат рендера/декода — растровое изображение, уже готовое под vision-OCR
/// (см. <see cref="ImageDownscaler"/>: длинная сторона ограничена, формат — JPEG).</summary>
public record DecodedImage(byte[] Bytes, string ContentType);

/// <summary>Ровно один из вариантов: печатный текст (офисные документы, txt/csv/rtf/html,
/// PDF с текстовым слоем) ИЛИ набор изображений (фото, PDF-скан без текстового слоя) —
/// см. докстринг <see cref="DocumentSourceKind"/>. <see cref="Kind"/> == Unsupported, когда
/// формат распознан, но не читается (легаси .doc, битый файл, неизвестная кодировка).</summary>
public record DocumentContent(DocumentSourceKind Kind, string? Text, IReadOnlyList<DecodedImage> Images, string? UnsupportedReason = null)
{
    public static DocumentContent FromText(string text) => new(DocumentSourceKind.Text, text, []);

    public static DocumentContent FromImages(IReadOnlyList<DecodedImage> images) => new(DocumentSourceKind.Image, null, images);

    public static DocumentContent Unsupported(string reason) => new(DocumentSourceKind.Unsupported, null, [], reason);
}

/// <summary>Какой путь конвейера извлечения обрабатывает документ дальше — текстовый (дёшево,
/// точно) или vision-OCR (дорого по контексту, нужен только для сканов). Именно поэтому
/// диспетчеризация выполняется здесь, а не отдаётся модели решать самой.</summary>
public enum DocumentSourceKind { Text, Image, Unsupported }
