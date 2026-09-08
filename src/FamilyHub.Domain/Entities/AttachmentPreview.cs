using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Один сгенерированный превью-артефакт вложения (см. AttachmentPreviewKind — их всего три вида,
/// не по одному на страницу документа). Регенерируемый кэш: EncryptionRotationJob при ротации
/// ключа не перешифровывает эти блобы, а удаляет их вместе со строкой (см. докстринг в
/// EncryptionRotationJob) — превью просто пересоздаётся по требованию при следующем открытии.
/// Поэтому здесь нет собственной видимости (как и у FileAttachment) — доступ проверяется через
/// родительское вложение той же цепочкой, что и /file.
/// </summary>
public class AttachmentPreview
{
    public Guid Id { get; set; }

    public Guid AttachmentId { get; set; }

    public AttachmentPreviewKind Kind { get; set; }

    /// <summary>Ключ в объектном хранилище (MinIO) — та же непрозрачная схема, что у оригинала,
    /// см. StorageKeyFactory.CreatePreviewKey.</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Только для Kind=Pdf — сколько страниц в артефакте, чтобы фронт мог нарисовать
    /// постраничную навигацию до того, как pdf.js сам откроет файл.</summary>
    public int? PageCount { get; set; }

    /// <summary>Блоб зашифрован тем же IFileCipher, что и оригиналы вложений — превью тоже может
    /// нести ПДн (текст документа виден на растровой странице).</summary>
    public bool IsEncrypted { get; set; }

    public string? KeyId { get; set; }

    public DateTime CreatedAt { get; set; }
}
