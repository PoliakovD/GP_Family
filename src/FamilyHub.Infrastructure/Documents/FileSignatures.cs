namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// Сверка заявленного клиентом ContentType с сигнатурой (magic bytes) файла — аудит
/// security-audit-2026-10, M2/бэклог. Тип при загрузке присылает браузер, и раньше он принимался на
/// веру: под видом image/png можно было положить что угодно, и это «что угодно» потом уходило в
/// декодеры картинок, PDF-растеризатор и LibreOffice. Отдача уже защищена nosniff + sandbox-CSP, а эта
/// проверка не пускает несоответствующий файл в конвейер обработки вовсе.
///
/// Текстовые форматы (txt/csv/html/xml) сигнатуры не имеют и не проверяются — их обрабатывают только
/// текстовые читатели.
/// </summary>
public static class FileSignatures
{
    /// <summary>Сколько первых байт файла нужно для проверки любого поддерживаемого формата.</summary>
    public const int HeadLength = 16;

    /// <returns>true — сигнатура соответствует типу или тип не имеет сигнатуры (текстовый);
    /// false — тип бинарный, а содержимое ему не соответствует.</returns>
    public static bool Matches(string contentType, ReadOnlySpan<byte> head)
    {
        var type = contentType.ToLowerInvariant();
        return type switch
        {
            DocumentContentTypes.Pdf => StartsWith(head, "%PDF-"u8),
            DocumentContentTypes.Jpeg => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
            DocumentContentTypes.Png => StartsWith(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            DocumentContentTypes.Webp => head.Length >= 12 && StartsWith(head, "RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
            DocumentContentTypes.Tiff => IsTiff(head),
            DocumentContentTypes.Heic => IsHeif(head),
            DocumentContentTypes.Docx or DocumentContentTypes.Xlsx => StartsWith(head, [0x50, 0x4B, 0x03, 0x04]),
            DocumentContentTypes.Doc or DocumentContentTypes.Xls => StartsWith(head, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]),
            DocumentContentTypes.Rtf => StartsWith(head, @"{\rtf"u8),
            _ => true,
        };
    }

    /// <summary>TIFF и BigTIFF: II*\0, MM\0*, II+\0, MM\0+.</summary>
    public static bool IsTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && ((data[0] == 'I' && data[1] == 'I' && data[3] == 0 && (data[2] == 42 || data[2] == 43))
            || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && (data[3] == 42 || data[3] == 43)));

    /// <summary>Только классический TIFF (II*\0 / MM\0*), без BigTIFF.</summary>
    public static bool IsClassicTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0)
            || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42));

    /// <summary>HEIC/HEIF: ISO BMFF-бокс ftyp с брендом семейства HEIF (размер бокса — первые 4 байта).</summary>
    private static bool IsHeif(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12 || !head[4..8].SequenceEqual("ftyp"u8)) return false;
        var brand = head[8..12];
        return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
            || brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);
}
