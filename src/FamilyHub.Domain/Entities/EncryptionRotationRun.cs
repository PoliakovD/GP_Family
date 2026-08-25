using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Один фоновый прогон перешифровки данных активным ключом (ADR-0009, EncryptionRotationJob).
/// Инфраструктурное состояние, не персональные данные — живёт в схеме public, как outbox/
/// DataProtectionKeys. Не более одной строки со Status=Running одновременно (частичный
/// уникальный индекс, см. EncryptionRotationRunConfiguration) — второй клик "Перешифровать" или
/// параллельный тик ночного добивателя видит уже идущий прогон и присоединяется к нему, а не
/// стартует новый.
///
/// Прогон состоит из двух последовательных фаз, каждая — со своим резюмируемым курсором:
/// 1. Поля (EncryptionRotationJob.FieldEntityTypes) — все [Encrypted]-свойства сущностей БД.
/// 2. Блобы вложений в MinIO (FileAttachment, где IsEncrypted=true).
/// Обе фазы идемпотентны: перезапись строки/блоба уже активным ключом (напр. после рестарта
/// посреди страницы) — не ошибка, просто лишний, но безвредный проход.
/// </summary>
public class EncryptionRotationRun
{
    public Guid Id { get; set; }

    /// <summary>keyId, которым завершится перешифровка (IEncryptionKeyRing.ActiveKeyId на момент запуска).</summary>
    public string TargetKeyId { get; set; } = string.Empty;

    public EncryptionRotationStatus Status { get; set; } = EncryptionRotationStatus.Running;

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Выставляется админ-эндпоинтом отмены; джоба проверяет флаг между страницами и
    /// останавливается на первой удобной точке, не среди SaveChanges.</summary>
    public bool CancelRequested { get; set; }

    public string? LastError { get; set; }

    // --- Фаза 1: поля ---

    /// <summary>Индекс текущего типа сущности в EncryptionRotationJob.FieldEntityTypes.</summary>
    public int FieldsStepIndex { get; set; }

    /// <summary>Id последней обработанной строки текущего типа (для постраничного резюме).</summary>
    public Guid? FieldsCursorId { get; set; }

    public int FieldsProcessed { get; set; }

    public int FieldsTotal { get; set; }

    // --- Фаза 2: блобы вложений ---

    public Guid? BlobsCursorId { get; set; }

    public int BlobsProcessed { get; set; }

    public int BlobsTotal { get; set; }
}
