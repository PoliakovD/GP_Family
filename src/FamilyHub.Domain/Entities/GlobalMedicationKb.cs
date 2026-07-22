namespace FamilyHub.Domain.Entities;

/// <summary>
/// Обезличенный справочник препаратов (задача 2.6, наполняется AI-конвейером этапа 4).
/// ИНВАРИАНТ: никакого персонального контекста — ни user/family/person-полей, ни FK.
/// Ключ — нормализованное название препарата; знание о препарате едино для всех.
/// Инвариант охраняет KbIsolationGuardTests (рефлексия по EF-модели).
/// </summary>
public class GlobalMedicationKb
{
    public Guid Id { get; set; }

    /// <summary>Нормализованное название (lowercase, trimmed) — ключ дедупликации.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Обогащённые данные о препарате (jsonb): состав, показания, формы выпуска.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Источник знания (например, "ГРЛС", "инструкция производителя").</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
