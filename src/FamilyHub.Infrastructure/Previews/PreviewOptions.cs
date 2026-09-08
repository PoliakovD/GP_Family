namespace FamilyHub.Infrastructure.Previews;

/// <summary>Настройки генерации превью вложений (секция "Previews") — фоновая задача
/// AttachmentPreviewProcessor, конвейер AttachmentPreviewRenderer.</summary>
public class PreviewOptions
{
    public const string SectionName = "Previews";

    /// <summary>Общий рубильник. false — превью не генерируются вовсе (все вложения остаются
    /// PreviewStatus=Unsupported), вьюер деградирует в карточку «Скачать». Не влияет на
    /// сами вложения/распознавание — только на предпросмотр.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Сторона миниатюры для сетки вложений/списка записей.</summary>
    public int ThumbnailMaxDimension { get; set; } = 320;

    /// <summary>DPI рендера страницы PDF/Office-PDF под миниатюру — заметно ниже, чем при OCR
    /// (Extraction:RasterDpi), миниатюре не нужна читаемость текста.</summary>
    public int ThumbnailDpi { get; set; } = 96;

    /// <summary>Сторона нормализованной полной картинки (HEIC/TIFF → JPEG) — крупнее миниатюры,
    /// это то, что реально показывается во вьюере с зумом.</summary>
    public int PageMaxDimension { get; set; } = 1600;

    public int JpegQuality { get; set; } = 80;

    /// <summary>Базовый адрес сайдкара Gotenberg (LibreOffice) — пусто, если Office-превью
    /// выключено конфигом целиком (см. Enabled) либо сайдкар не поднят в этом окружении.</summary>
    public string GotenbergBaseUrl { get; set; } = string.Empty;

    public int GotenbergTimeoutSeconds { get; set; } = 30;
}
