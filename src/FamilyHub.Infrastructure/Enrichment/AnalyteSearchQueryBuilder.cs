using FamilyHub.Infrastructure.Prompts;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Строит сырой поисковый запрос для WebSearchTopic.LabAnalyte — один на оба провайдера
/// (Brave/Yandex), чтобы не разойтись формулировкой (пересборка enrich-пайплайна: раньше здесь
/// был SpecimenQueryLabel с захардкоженным switch по 6 значениям enum SpecimenType — источник
/// показателя теперь произвольная строка из справочника (GlobalSpecimenKb.DisplayName), код её не
/// классифицирует, только вставляет в запрос как есть). Результат кэшируется
/// (LabAnalyteSearchCacheService), поэтому лучше сразу перечислить всё нужное явно.
/// Шаблон редактируется из админки (см. class doc IPromptProvider, ключ "analysis.search-query") —
/// админ может переписать формулировку и сместить акценты без деплоя. Плейсхолдеры: {name} —
/// нормализованное название показателя, {specimen} — источник в скобках вида " (кровь)" либо
/// пустая строка, если источник не определён (подстановку скобок и пробела делает код, а не
/// шаблон, — админ не обязан вручную балансировать пробелы/скобки при пустом источнике).</summary>
public class AnalyteSearchQueryBuilder(IPromptProvider promptProvider)
{
    public const string FallbackTemplate =
        "{name}{specimen} анализ норма референсные значения у мужчин и женщин по возрасту единицы измерения";

    public async Task<string> BuildAsync(string normalizedName, string? specimenDisplayName, CancellationToken ct = default)
    {
        var template = await promptProvider.GetAsync("analysis.search-query", FallbackTemplate, ct);
        var specimenPart = string.IsNullOrWhiteSpace(specimenDisplayName) ? string.Empty : $" ({specimenDisplayName})";
        return template.Replace("{name}", normalizedName).Replace("{specimen}", specimenPart);
    }
}
