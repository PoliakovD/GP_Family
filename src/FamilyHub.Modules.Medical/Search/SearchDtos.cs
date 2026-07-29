namespace FamilyHub.Modules.Medical.Search;

/// <summary>Источник результата поиска — четыре независимых контура доступа (см. SearchService).</summary>
public enum SearchResultType { Medication, Kb, Record, Birthday }

public record SearchResultItem(
    SearchResultType Type, Guid Id, string Title, string? Snippet, double Score,
    MedicationContext? Medication = null, BirthdayContext? Birthday = null);

/// <summary>Контекст лекарства: где оно лежит и до какого срока годно — нужен UI Аптечки, чтобы
/// клик по результату поиска раскрывал ровно ту аптечку, а не просто список раздела.</summary>
public record MedicationContext(
    Guid FamilyId, string FamilyName, Guid MedkitId, string MedkitName, DateOnly? ExpiryDate);

/// <summary>Контекст дня рождения: в какой семье он записан и когда.</summary>
public record BirthdayContext(Guid FamilyId, string FamilyName, DateOnly Date);

public record SearchResponse(IReadOnlyList<SearchResultItem> Items);

/// <summary>Проекция сырого SQL-запроса к medical."Medications" (search_vector/similarity — вне EF-модели).</summary>
internal sealed class MedicationSearchRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public Guid MedkitId { get; set; }
    public string MedkitName { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public string FamilyName { get; set; } = string.Empty;
    public double Score { get; set; }
}

/// <summary>Проекция сырого SQL-запроса к kb.global_medications_kb.</summary>
internal sealed class KbSearchRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public double Score { get; set; }
}
