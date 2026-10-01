namespace FamilyHub.Domain.Entities;

/// <summary>
/// Ключ кэша платного поиска по показателю: (название, группа поиска биоматериала). Раньше ключом был
/// сам биоматериал, и АЧТВ × (кровь, венозная кровь, плазма) стоил три платных поиска, хотя в запрос
/// подставляется одно и то же слово. Теперь биоматериалы с одинаковым <see cref="GlobalSpecimenKb.SearchGroupKey"/>
/// делят одну строку кэша и один поисковый запрос; у биоматериала без группы (null) группа своя — не склеивается.
/// Записи в kb остаются отдельными на каждый биоматериал (нормы различаются, напр. глюкоза венозная/капиллярная).
/// </summary>
public static class SearchGroupKeys
{
    public const string GroupPrefix = "group:";
    public const string SpecimenPrefix = "specimen:";

    /// <summary>Нормализация введённой админом группы: обрезка и нижний регистр; пусто → null (своя группа).</summary>
    public static string? Normalize(string? groupKey)
    {
        var trimmed = groupKey?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Эффективный ключ группы поиска для биоматериала — то, что хранится в LabAnalyteSearchCache.SearchGroupKey.</summary>
    public static string Effective(Guid specimenKbId, string? specimenGroupKey) =>
        Normalize(specimenGroupKey) is { } g ? GroupPrefix + g : SpecimenPrefix + specimenKbId.ToString("D");
}
