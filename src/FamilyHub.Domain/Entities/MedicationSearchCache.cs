namespace FamilyHub.Domain.Entities;

/// <summary>
/// Кэш обращений к платному внешнему поиску (Yandex/Brave) — отдельно от самого справочника
/// (<see cref="GlobalMedicationKb"/>), потому что отслеживает факт ОБРАЩЕНИЯ к платному API, а не
/// факт успешной записи в справочник: неудачная суммаризация/отсутствие доверенных источников
/// всё равно расходует платную квоту и должна блокировать повторный запрос на минимальный
/// интервал, даже если строка в kb так и не появилась. Обезличено, как и GlobalMedicationKb —
/// только нормализованное название, никакого персонального контекста (см. KbIsolationGuardTests).
/// </summary>
public class MedicationSearchCache
{
    public Guid Id { get; set; }

    /// <summary>Тот же ключ, что и GlobalMedicationKb.NormalizedName — не FK (kb-таблицы не ссылаются
    /// друг на друга, см. инвариант изоляции), а совпадающее по смыслу нормализованное имя.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Провайдер последнего обращения (например, "Yandex").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Когда платный API был запрошен по этому названию в последний раз.</summary>
    public DateTime LastUpdatedAt { get; set; }

    /// <summary>LastUpdatedAt + минимальный интервал обновления (EnrichmentOptions.MinRefreshIntervalMonths) —
    /// хранится явно (не вычисляется на лету), чтобы проверка "можно ли обновить" была одним
    /// индексным сравнением, а не пересчётом даты на каждый запрос.</summary>
    public DateTime CanBeUpdatedAfter { get; set; }

    /// <summary>Сериализованный List&lt;WebSnippet&gt; — сами результаты последнего платного поиска,
    /// а не только факт обращения. Это и делает таблицу настоящим кэшем: пока не истёк
    /// CanBeUpdatedAfter, суммаризатор можно пересчитывать сколько угодно раз (например, при
    /// доработке промпта/схемы полей MedicationSummary в разработке) без повторной оплаты
    /// внешнего запроса — см. MedicationSearchCacheService/MedicationEnrichmentProcessor.</summary>
    public string? SnippetsJson { get; set; }
}
