using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Attachments;

/// <summary>ExtractedAt — когда конвейер извлечения (ветка medicalrecords) последний раз успешно
/// распознал этот файл, null если ещё ни разу; фронт использует это, чтобы решать, показывать ли
/// кнопку «Распознать» на записи (нечего делать, если у всех вложений ExtractedAt уже заполнен).
/// PreviewStatus — фронт использует, чтобы решать, показывать спиннер «Готовим превью…» (Pending),
/// сразу открывать вьюер (Ready) или карточку «Скачать» без похода за /preview (Unsupported/Failed).</summary>
public record AttachmentDto(
    Guid Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt, DateTime? ExtractedAt,
    AttachmentPreviewStatus PreviewStatus);

/// <summary>Лимиты загрузки, отдаются фронту заранее — чтобы дизейблить кнопку/показывать
/// «осталось N из 8» до попытки загрузки, а не только по факту отказа 409/413.</summary>
public record AttachmentLimitsDto(long MaxFileSizeBytes, int MaxFilesPerRecord);

public enum AttachmentAccessResult { Success, Forbidden, NotFound, TooLarge, UnsupportedContentType, TooManyFiles }

/// <summary>Что вьюер должен нарисовать — сервер уже решил это за клиента (какой артефакт есть,
/// какой из них главный), фронту не нужно знать про AttachmentPreviewKind вовсе.</summary>
public enum AttachmentRenderKind
{
    /// <summary>Показывать нечем — карточка «Скачать» (Pending/Failed/Unsupported или сам формат
    /// не подразумевает предпросмотра, напр. .zip).</summary>
    None,

    /// <summary>ContentUrl ведёт на PDF (оригинал или Office→PDF) — рисуется pdf.js.</summary>
    Pdf,

    /// <summary>ContentUrl ведёт на изображение (оригинал jpeg/png/webp или нормализованная
    /// страница heic/tiff) — рисуется &lt;img&gt; с зумом/поворотом.</summary>
    Image,

    /// <summary>ContentUrl отдаёт текст (txt/csv/xml) — рисуется &lt;pre&gt;/таблица на клиенте.</summary>
    Text,
}

/// <summary>Ответ GET /api/attachments/{id}/preview — уже готовые подписанные ссылки, фронту не
/// нужно самому знать, какой эндпоинт какой scope ожидает.</summary>
public record AttachmentPreviewDto(
    AttachmentPreviewStatus Status,
    AttachmentRenderKind RenderKind,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? PageCount,
    string? ThumbnailUrl,
    string? ContentUrl,
    string DownloadUrl,
    string? FailureReason);
