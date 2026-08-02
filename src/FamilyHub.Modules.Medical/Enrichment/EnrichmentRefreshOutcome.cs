namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>Итог ручного запроса «Уточнить в справочнике». Задача ставится в очередь всегда
/// (кроме пустого имени) — MedicationEnrichmentProcessor сам решает, переиспользовать ли уже
/// закэшированные результаты платного поиска (MedicationSearchCache) или обратиться к API заново,
/// так что отдельного статуса "на кулдауне" здесь больше не нужно (см. MedicationSearchCacheService:
/// это настоящий кэш сниппетов, а не просто отметка "когда можно/нельзя").</summary>
public enum EnrichmentRefreshStatus { Requested, NothingToRefresh }

public record EnrichmentRefreshOutcome(EnrichmentRefreshStatus Status, DateTime? AvailableAt)
{
    public static EnrichmentRefreshOutcome Requested() => new(EnrichmentRefreshStatus.Requested, null);

    public static EnrichmentRefreshOutcome NothingToRefresh() => new(EnrichmentRefreshStatus.NothingToRefresh, null);
}
