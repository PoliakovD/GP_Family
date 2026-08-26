using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Один показатель, извлечённый из бланка анализа (ветка medicalrecords, задача 5.2). Строка на
/// (запись, показатель) — один <see cref="MedicalRecord"/> с несколькими показателями даёт
/// несколько строк. Наследует видимость родительской записи, своей не имеет — тот же принцип,
/// что у <see cref="FileAttachment"/> (см. MedicalRecordService.IsVisibleToAsync).
///
/// <see cref="AnalyteKey"/>/<see cref="Flag"/> — plaintext (по ним идут поиск, тренд и SQL-выборка
/// "мои показатели"), сами значения и референсы — [Encrypted]: раскрытие БД называет, какие
/// анализы сдавались и было ли отклонение, но не конкретные цифры. [Encrypted] шифрует только
/// string-колонки (см. AppDbContext.OnModelCreating), поэтому значение хранится строкой как
/// напечатано (<see cref="ValueRaw"/>) плюс отдельно распарсенное число (<see cref="ValueNumericText"/>),
/// а не как double.
/// </summary>
public class LabIndicator
{
    public Guid Id { get; set; }

    public Guid MedicalRecordId { get; set; }

    /// <summary>Денормализация даты записи — тренд по показателю строится без джойна к MedicalRecords.</summary>
    public DateOnly RecordDate { get; set; }

    /// <summary>Владелец записи (не FK, как и MedicalRecord.OwnerUserId) — ключ выборки "мои показатели".</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Нормализованное имя показателя (см. LabAnalyteNormalizer) — ключ группировки,
    /// поиска и тренда. "Гемоглобин (HGB), г/л" → "гемоглобин".</summary>
    public string AnalyteKey { get; set; } = string.Empty;

    /// <summary>Имя как напечатано в бланке — для отображения.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Привязка к kb.global_lab_analytes_kb, если найдена (не FK — справочник в другой
    /// схеме, физически изолирован).</summary>
    public Guid? KbAnalyteId { get; set; }

    public IndicatorFlag Flag { get; set; } = IndicatorFlag.Unknown;

    /// <summary>Откуда взят референс, использованный для Flag — каскад приоритетов, см. RefSource.</summary>
    public RefSource RefSource { get; set; } = RefSource.None;

    /// <summary>Биоматериал (кровь/моча/кал и т.д.) — часть ключа группировки на графике/в списке
    /// "мои показатели" вместе с AnalyteKey, иначе одноимённые показатели из разных биоматериалов
    /// (лейкоциты крови и мочи) смешались бы на одном тренде. См. FamilyHub.Domain.Enums.SpecimenType.</summary>
    public SpecimenType Specimen { get; set; } = SpecimenType.Unknown;

    /// <summary>Порядок в бланке — таблица показателей на фронте отображается в исходном порядке,
    /// не алфавитном.</summary>
    public int Position { get; set; }

    /// <summary>Значение как напечатано ("118", "отрицательно", "не обнаружено").</summary>
    [Encrypted]
    public string ValueRaw { get; set; } = string.Empty;

    /// <summary>Числовое значение (InvariantCulture), если ValueRaw парсится как число — null для
    /// качественных результатов ("отрицательно"). Флаг считается по этому полю, когда оно есть.</summary>
    [Encrypted]
    public string? ValueNumericText { get; set; }

    [Encrypted]
    public string? Unit { get; set; }

    [Encrypted]
    public string? RefLowText { get; set; }

    [Encrypted]
    public string? RefHighText { get; set; }

    /// <summary>Референс как напечатан целиком, если не раскладывается на low/high ("отрицательно",
    /// "1-3 в п/зр") — RefLowText/RefHighText в этом случае пустые.</summary>
    [Encrypted]
    public string? RefText { get; set; }

    public DateTime CreatedAt { get; set; }
}
