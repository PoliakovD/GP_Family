using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Мед-запись — персональный ресурс. Принадлежит пользователю (OwnerUserId), НЕ семье.
/// По умолчанию приватен. НЕ реализует IFamilyOwned — видимость определяется
/// FamilyMedicalShare + MedicalRecordHidden, а не ролью в семье.
/// ПДн-поля шифруются at-rest (этап 2): в БД запись обезличена до OwnerUserId (UUID).
/// Двух видов (Kind): анализ или посещение врача — единая таблица, единый контур доступа
/// (шаринг/скрытие/аудит/вложения не различают вид, см. MedicalRecordService).
/// </summary>
public class MedicalRecord
{
    public Guid Id { get; set; }

    /// <summary>Владелец записи. Только он управляет шарингом и скрытием.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Анализ или посещение врача. Не шифруется — по нему фильтруются списки и поиск в SQL.</summary>
    public MedicalRecordKind Kind { get; set; }

    [Encrypted]
    public string PersonName { get; set; } = string.Empty;

    public DateOnly RecordDate { get; set; }

    [Encrypted]
    public string? Doctor { get; set; }

    [Encrypted]
    public string? Description { get; set; }

    /// <summary>Заготовка под OCR-конвейер (задачи 5.2/5.3 — пока не реализован): структурированный
    /// результат распознавания бланка анализа/заключения врача. [Encrypted] ⇒ хранится строкой,
    /// не jsonb: SQL-фильтрация по содержимому невозможна по построению (ADR-0002). Индексация
    /// показателей для поиска — отдельная задача.</summary>
    [Encrypted]
    public string? ExtractedDataJson { get; set; }

    public ExtractionStatus ExtractionStatus { get; set; }

    public DateTime CreatedAt { get; set; }
}
