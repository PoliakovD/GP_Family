namespace FamilyHub.Infrastructure.Documents;

/// <summary>Конфигурация конвейера извлечения (ветка medicalrecords, задачи 5.2/5.3) — секция
/// <c>Extraction</c>, биндится в Program.cs рядом с <c>LmStudioOptions</c>.</summary>
public class ExtractionOptions
{
    public const string SectionName = "Extraction";

    /// <summary>Общий рубильник конвейера — при false в DI остаётся заглушка
    /// (NullMedicalDocumentExtractor), как и Enrichment:Provider=Null для справочника.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Верхняя граница страниц PDF-скана, которые рендерятся в картинки — защита от
    /// многостраничной выписки, положенной по ошибке (сотни страниц), от перегрузки локальной
    /// модели одним документом.</summary>
    public int MaxPages { get; set; } = 10;

    /// <summary>Длинная сторона изображения перед отправкой в LM Studio, px.</summary>
    public int MaxImageDimension { get; set; } = 1600;

    public int JpegQuality { get; set; } = 78;

    /// <summary>Размер текстового чанка при разбиении длинного документа на куски под один
    /// вызов модели.</summary>
    public int MaxCharsPerChunk { get; set; } = 6000;

    /// <summary>DPI рендера страницы PDF-скана — компромисс между читаемостью мелкого шрифта
    /// бланка и итоговым размером картинки (сжимается ImageDownscaler'ом уже после рендера).</summary>
    public int RasterDpi { get; set; } = 150;
}
