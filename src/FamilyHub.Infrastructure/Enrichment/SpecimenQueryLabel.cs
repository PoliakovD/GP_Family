using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Биоматериал в предложном падеже ("в крови", "в моче") для сырого поискового запроса
/// (пересборка enrich-пайплайна анализов, см. IMedicationSearchProvider.SearchAsync) — делает
/// запрос информативнее и однозначнее, что важно: он кэшируется (LabAnalyteSearchCacheService) и
/// оплачивается один раз, так что стоит сформулировать его как можно точнее сразу. Пустая строка
/// для Unknown/Other — биоматериал не определён или не входит в обычный набор, обычный (без
/// уточнения) запрос безопаснее выдуманной формулировки.</summary>
public static class SpecimenQueryLabel
{
    public static string InPrepositionalCase(SpecimenType specimen) => specimen switch
    {
        SpecimenType.Blood => "в крови",
        SpecimenType.Urine => "в моче",
        SpecimenType.Stool => "в кале",
        SpecimenType.VaginalSwab => "в вагинальном мазке",
        SpecimenType.Saliva => "в слюне",
        _ => string.Empty,
    };

    /// <summary>Общий текст поискового запроса для WebSearchTopic.LabAnalyte — один на оба
    /// провайдера (Brave/Yandex), чтобы не разойтись формулировкой. Результат кэшируется
    /// (LabAnalyteSearchCacheService), поэтому лучше сразу перечислить всё нужное явно.</summary>
    public static string BuildAnalyteSearchQuery(string normalizedName, SpecimenType specimen)
    {
        var specimenLabel = InPrepositionalCase(specimen);
        var specimenPart = specimenLabel.Length > 0 ? $" {specimenLabel}" : string.Empty;
        return $"{normalizedName} анализ{specimenPart} норма референсные значения у мужчин и женщин по возрасту единицы измерения";
    }
}
