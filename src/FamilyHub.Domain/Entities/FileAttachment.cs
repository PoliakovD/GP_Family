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

    /// <summary>Имя файла от пользователя — может содержать ФИО/диагноз, поэтому шифруется.</summary>
    [Encrypted]
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Блоб в хранилище зашифрован IFileCipher (этап 2); false — legacy-файлы до шифрования.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>
    /// keyId, которым зашифрован блоб (тот же, что зашит в заголовок FHE1 внутри самого блоба —
    /// см. AesGcmFileCipher) — null для незашифрованных вложений (IsEncrypted=false). Денормализация
    /// ради ADR-0009: без неё «сколько блобов ещё не перешифровано» требовало бы скачивать и
    /// парсить заголовок каждого объекта из MinIO вместо SELECT по колонке.
    /// EncryptionRotationJob обновляет её вместе с перезаливкой блоба под новым активным ключом.
    /// </summary>
    public string? KeyId { get; set; }

    public DateTime UploadedAt { get; set; }
}
