namespace FamilyHub.Modules.Medical.Attachments;

public record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt);

/// <summary>Лимиты загрузки, отдаются фронту заранее — чтобы дизейблить кнопку/показывать
/// «осталось N из 8» до попытки загрузки, а не только по факту отказа 409/413.</summary>
public record AttachmentLimitsDto(long MaxFileSizeBytes, int MaxFilesPerRecord);

public enum AttachmentAccessResult { Success, Forbidden, NotFound, TooLarge, UnsupportedContentType, TooManyFiles }
