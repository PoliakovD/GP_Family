namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// Единый allow-list форматов, которые понимает конвейер извлечения (ветка medicalrecords,
/// задачи 5.2/5.3): офисные документы + фото/PDF. HL7/FHIR/СЭМД — вне объёма (см. план).
/// <see cref="FamilyHub.Modules.Medical.Attachments.AttachmentService.AllowedContentTypes"/>
/// расширяется этим же списком — единая точка правды для того, что вообще можно приложить
/// к мед-записи, и для того, что конвейер умеет распознать.
/// </summary>
public static class DocumentContentTypes
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";
    public const string Heic = "image/heic";
    public const string Tiff = "image/tiff";
    public const string Pdf = "application/pdf";
    public const string Doc = "application/msword";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Xls = "application/vnd.ms-excel";
    public const string Csv = "text/csv";
    public const string PlainText = "text/plain";
    public const string Rtf = "application/rtf";
    public const string Html = "text/html";
    public const string Xml = "application/xml";
    public const string XmlText = "text/xml";

    /// <summary>Растровые форматы — путь через vision-OCR. SkiaSharp на Linux не декодирует ни
    /// HEIC, ни TIFF: TIFF <see cref="ImageDownscaler"/> дешифрует фолбэком через
    /// SixLabors.ImageSharp (чисто управляемый декодер, без риска нативных musl-биндингов), а
    /// HEIC остаётся нераспознаваемым сервером — см. докстринг ImageDownscaler.</summary>
    public static readonly IReadOnlySet<string> Images =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Jpeg, Png, Webp, Heic, Tiff };

    /// <summary>NPOI умеет распознать — XSSF/HSSF/XWPF. Легаси .doc (HWPF) сюда намеренно НЕ
    /// входит: в NPOI 2.7.4 модуль HWPF отсутствует во всех целевых сборках пакета (проверено
    /// рефлексией при подключении зависимости), а не просто не подключён нами. Полноценная
    /// .doc-поддержка потребовала бы стороннего конвертера (LibreOffice headless и т.п.) — вне
    /// объёма этой ветки.</summary>
    public static readonly IReadOnlySet<string> Office =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Docx, Xlsx, Xls };

    public static readonly IReadOnlySet<string> PlainTextLike =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Csv, PlainText, Rtf, Html, Xml, XmlText };

    /// <summary>Полный allow-list вложений мед-записи (что можно ЗАГРУЗИТЬ) — включает Doc, хотя
    /// его конвейер извлечения не распознаёт (см. Office выше): пользователь всё равно может
    /// приложить .doc-файл для хранения/скачивания, просто «Распознать» на нём вернёт Failed
    /// с понятной причиной вместо тихого отказа принять файл вовсе.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        Images.Concat(Office).Concat(PlainTextLike).Append(Pdf).Append(Doc),
        StringComparer.OrdinalIgnoreCase);
}
