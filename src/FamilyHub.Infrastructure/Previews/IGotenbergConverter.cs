namespace FamilyHub.Infrastructure.Previews;

/// <summary>
/// Конвертация офисных документов (docx/xlsx/xls/doc/rtf/html) в PDF под точную вёрстку превью —
/// у NPOI (уже используется для извлечения текста, см. OfficeDocumentReader) нет движка вёрстки,
/// пиксель-в-пиксель требует настоящий LibreOffice. NullGotenbergConverter — когда сайдкар не
/// настроен/выключен: Office-документы деградируют в карточку «Скачать» вместо падения задачи.
/// </summary>
public interface IGotenbergConverter
{
    /// <summary>null — конвертация не удалась/не настроена; вызывающий код (AttachmentPreviewRenderer)
    /// трактует это как обычный неуспех рендера, не как исключение.</summary>
    Task<byte[]?> ConvertToPdfAsync(byte[] content, string contentType, CancellationToken ct = default);

    /// <summary>Готовая HTML-страница → PDF через Chromium-маршрут Gotenberg (отчёт для врача): A4,
    /// печать фона, необязательный колонтитул с номерами страниц. null — сайдкар не настроен/недоступен/
    /// отказал; вызывающий код (DoctorReportService) превращает это в понятную ошибку пользователю.</summary>
    Task<byte[]?> ConvertHtmlToPdfAsync(string html, string? footerHtml = null, CancellationToken ct = default);
}
