namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Название строки кэша для людей (DisplayName): пробелы по краям срезаются, пустое — null, длинное —
/// обрезается до колонки. Не ключ — см. LabAnalyteSearchCache.DisplayName.</summary>
public static class SearchCacheDisplayName
{
    public const int MaxLength = 300;

    public static string? Clean(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }
}
