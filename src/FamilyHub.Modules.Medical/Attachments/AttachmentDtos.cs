namespace FamilyHub.Modules.Medical.Attachments;

public record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt);

public enum AttachmentAccessResult { Success, Forbidden, NotFound }
