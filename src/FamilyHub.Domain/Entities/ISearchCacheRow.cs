namespace FamilyHub.Domain.Entities;

/// <summary>
/// Общая форма двух таблиц кэша платного поиска (<see cref="MedicationSearchCache"/>,
/// <see cref="LabAnalyteSearchCache"/>) — нужна очереди «Одобрение» (ADR-0018), которая правит набор
/// сниппетов (ручное добавление, закрепление, удаление, override) одним кодом для обоих справочников.
/// </summary>
public interface ISearchCacheRow
{
    Guid Id { get; }
    string NormalizedName { get; }

    /// <summary>Название для людей (не ключ) — см. LabAnalyteSearchCache.DisplayName.</summary>
    string? DisplayName { get; set; }
    string Provider { get; set; }
    DateTime LastUpdatedAt { get; set; }
    DateTime CanBeUpdatedAfter { get; set; }

    /// <summary>Сериализованный List&lt;WebSnippet&gt; (см. SearchCacheSnippets).</summary>
    string? SnippetsJson { get; set; }

    /// <summary>Сериализованный Dictionary&lt;string Url, bool Enabled&gt; — override'ы админа.</summary>
    string? OverridesJson { get; set; }
}
