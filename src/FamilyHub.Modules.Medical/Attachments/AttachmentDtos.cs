namespace FamilyHub.Modules.Medical.Attachments;

/// <summary>ExtractedAt — когда конвейер извлечения (ветка medicalrecords) последний раз успешно
/// распознал этот файл, null если ещё ни разу; фронт использует это, чтобы решать, показывать ли
/// кнопку «Распознать» на записи (нечего делать, если у всех вложений ExtractedAt уже заполнен).</summary>
public record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt, DateTime? ExtractedAt);

/// <summary>Лимиты загрузки, отдаются фронту заранее — чтобы дизейблить кнопку/показывать
/// «осталось N из 8» до попытки загрузки, а не только по факту отказа 409/413.</summary>
public record AttachmentLimitsDto(long MaxFileSizeBytes, int MaxFilesPerRecord);

public enum AttachmentAccessResult { Success, Forbidden, NotFound, TooLarge, UnsupportedContentType, TooManyFiles }
