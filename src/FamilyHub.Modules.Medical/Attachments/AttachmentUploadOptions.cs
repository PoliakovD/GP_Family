namespace FamilyHub.Modules.Medical.Attachments;

/// <summary>Лимиты загрузки вложений к мед-записям — секция "Attachments" (та же секция, что и
/// у AttachmentDownloadOptions: разные POCO под разные группы настроек одного раздела конфига).</summary>
public class AttachmentUploadOptions
{
    public const string SectionName = "Attachments";

    /// <summary>Максимальный размер одного файла. Env: <c>Attachments__MaxFileSizeBytes</c>.</summary>
    public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Максимум вложений на одну мед-запись (анализ или приём врача) — не даёт
    /// превратить одну запись в бездонное хранилище. Env: <c>Attachments__MaxFilesPerRecord</c>.</summary>
    public int MaxFilesPerRecord { get; set; } = 8;
}
