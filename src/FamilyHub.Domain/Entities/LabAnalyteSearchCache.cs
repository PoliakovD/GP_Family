namespace FamilyHub.Domain.Entities;

/// <summary>
/// Кэш обращений к платному внешнему поиску для лабораторных показателей — зеркало
/// <see cref="MedicationSearchCache"/> (пересборка enrich-пайплайна анализов, закрывает
/// задокументированный ранее пропуск: раньше повторный прогон/доработка промпта суммаризатора
/// означала новый платный запрос на каждый показатель). Ключ — пара (NormalizedName, SpecimenKbId),
/// не одно имя: "белок" в крови и в моче ищутся и кэшируются раздельно. Обезличено, как и
/// GlobalLabAnalyteKb — только нормализованное название и источник, никакого персонального
/// контекста (см. KbIsolationGuardTests).
/// </summary>
public class LabAnalyteSearchCache
{
    public Guid Id { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    public Guid SpecimenKbId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public DateTime LastUpdatedAt { get; set; }

    /// <summary>LastUpdatedAt + минимальный интервал обновления (EnrichmentOptions.MinRefreshIntervalMonths).</summary>
    public DateTime CanBeUpdatedAfter { get; set; }

    /// <summary>Сериализованный List&lt;WebSnippet&gt; — ВСЕ результаты последнего платного поиска,
    /// включая недоверенные (см. MedicationSearchCache.SnippetsJson — тот же принцип).</summary>
    public string? SnippetsJson { get; set; }

    /// <summary>Точечные ручные исключения/включения конкретных сниппетов из админки — тот же
    /// формат и та же гарантия сохранности при обновлении, что MedicationSearchCache.OverridesJson.</summary>
    public string? OverridesJson { get; set; }
}
