using FamilyHub.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Провайдер по умолчанию (Enrichment:Provider не задан или "Null") — наружу не уходит НИ ОДНОГО
/// запроса, зеркало LoggingEmailSender/LoggingNotificationSender в этом проекте. Без явного
/// конфига обогащение справочника молча не работает (пустой список сниппетов → суммаризатор
/// отклоняет задачу без источников → job → Failed), а не падает или тихо начинает ходить в интернет.
/// </summary>
public class NullMedicationSearchProvider(ILogger<NullMedicationSearchProvider> logger) : IMedicationSearchProvider
{
    public string Name => "Null";

    public Task<IReadOnlyList<WebSnippet>> SearchAsync(
        string normalizedName, WebSearchTopic topic = WebSearchTopic.Medication,
        SpecimenType specimen = SpecimenType.Unknown, CancellationToken ct = default)
    {
        logger.LogWarning(
            "Enrichment:Provider не настроен — внешний поиск по «{NormalizedName}» пропущен, справочник не обогащается.",
            normalizedName);
        return Task.FromResult<IReadOnlyList<WebSnippet>>([]);
    }
}
