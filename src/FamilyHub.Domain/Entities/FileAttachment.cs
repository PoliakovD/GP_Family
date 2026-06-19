using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Метаданные скана; сам файл лежит в MinIO. Доступ наследуется от родительской
/// записи (MedicalRecord/Medication) — своей видимости у вложения нет.
/// </summary>
public class FileAttachment
{
    public Guid Id { get; set; }

    public FileOwnerType OwnerType { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>Ключ в объектном хранилище (MinIO).</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Закладка под шифрование медданных (152-ФЗ).</summary>
    public bool IsEncrypted { get; set; }

    public DateTime UploadedAt { get; set; }
}
