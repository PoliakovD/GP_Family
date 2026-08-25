namespace FamilyHub.Modules.Medical.Search;

/// <summary>Источник результата поиска — шесть независимых контуров доступа (см. SearchService).
/// Новые значения — строго в конец: фронт сравнивает и присылает числовые значения
/// (models/types.ts), перенумеровывать существующие нельзя. Indicator (ветка medicalrecords)
/// добавлен последним по той же причине, по которой раньше последним был Visit.</summary>
public enum SearchResultType { Medication, Kb, Record, Birthday, Visit, Indicator }

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

/// <summary>Проекция сырого SQL-запроса к medical."LabIndicators" (ветка medicalrecords) —
/// AnalyteKey/Flag не зашифрованы, поэтому триграммный поиск идёт прямо в SQL, как у медикаментов.</summary>
internal sealed class IndicatorSearchRow
{
    public Guid Id { get; set; }
    public string AnalyteKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Flag { get; set; }
    public DateOnly RecordDate { get; set; }
    public double Score { get; set; }
}
