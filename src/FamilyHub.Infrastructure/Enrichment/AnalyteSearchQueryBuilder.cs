namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Строит сырой поисковый запрос для WebSearchTopic.LabAnalyte — один на оба провайдера
/// (Brave/Yandex), чтобы не разойтись формулировкой (пересборка enrich-пайплайна: раньше здесь
/// был SpecimenQueryLabel с захардкоженным switch по 6 значениям enum SpecimenType — источник
/// показателя теперь произвольная строка из справочника (GlobalSpecimenKb.DisplayName), код её не
/// классифицирует, только вставляет в запрос как есть). Результат кэшируется
/// (LabAnalyteSearchCacheService), поэтому лучше сразу перечислить всё нужное явно.</summary>
public static class AnalyteSearchQueryBuilder
{
    public static string Build(string normalizedName, string? specimenDisplayName)
    {
        var specimenPart = string.IsNullOrWhiteSpace(specimenDisplayName) ? string.Empty : $" ({specimenDisplayName})";
        return $"{normalizedName}{specimenPart} анализ норма референсные значения у мужчин и женщин по возрасту единицы измерения";
    }
}
