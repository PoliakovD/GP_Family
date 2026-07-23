namespace FamilyHub.Modules.Medical.Search;

/// <summary>Источник результата поиска — три независимых контура доступа (см. SearchService).</summary>
public enum SearchResultType { Medication, Kb, Record }

public record SearchResultItem(SearchResultType Type, Guid Id, string Title, string? Snippet, double Score);

public record SearchResponse(IReadOnlyList<SearchResultItem> Items);

/// <summary>Проекция сырого SQL-запроса к medical."Medications" (search_vector/similarity — вне EF-модели).</summary>
internal sealed class MedicationSearchRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Score { get; set; }
}

/// <summary>Проекция сырого SQL-запроса к kb.global_medications_kb.</summary>
internal sealed class KbSearchRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public double Score { get; set; }
}
