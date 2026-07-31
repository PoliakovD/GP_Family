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

    /// <summary>
    /// Торговые названия/синонимы одного и того же препарата (например, "нурофен" для
    /// "ибупрофен") — этап 4, попадание за один индексный лукап без нечёткого поиска.
    /// НЕ заведено в EF-модель (как search_vector): Postgres text[] с GIN-индексом не имеет
    /// кроссплатформенного (Npgsql/SQLite-юнит-тесты) маппинга — читается/пишется только raw SQL
    /// (см. KbLookupService/KbWriter), поэтому это поле здесь чисто документирующее.
    /// </summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>Версия схемы <see cref="PayloadJson"/> — позволяет мигрировать формат знания без потери строк.</summary>
    public int PayloadVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
