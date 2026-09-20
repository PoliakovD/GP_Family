namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Конфигурация секции "Enrichment" в appsettings — внешний веб-поиск для обогащения
/// справочника (этап 4). Доверенные домены (раньше — TrustedDomains/AnalyteTrustedDomains здесь)
/// переехали в БД (EnrichmentTrustedDomain, пересборка enrich-пайплайна) — управляются через
/// админку без передеплоя; начальные значения — миграция AddEnrichmentTrustedDomains.</summary>
public class EnrichmentOptions
{
    public const string SectionName = "Enrichment";

    /// <summary>Без явного конфига — Null (наружу не уходит ничего, см. NullMedicationSearchProvider).</summary>
    public MedicationSearchProviderKind Provider { get; set; } = MedicationSearchProviderKind.Null;

    /// <summary>Ключ провайдера. Обязателен, если Provider != Null (fail-fast в Program.cs).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Yandex Cloud folder ID — обязателен, если Provider=Yandex (Web Search API v2/gen/search
    /// требует folderId в теле каждого запроса, fail-fast в Program.cs).</summary>
    public string? FolderId { get; set; }

    /// <summary>Минимальный интервал между обращениями к платному API за ОДНО и то же нормализованное
    /// название (см. MedicationSearchCache) — защита от повторной оплаты за один и тот же препарат.</summary>
    public int MinRefreshIntervalMonths { get; set; } = 1;

    /// <summary>Сколько сниппетов максимум передаём суммаризатору за один запрос.</summary>
    public int MaxSnippets { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Цена одного платного вызова в рублях — только для отображения в админке
    /// («Вызовы поиска» → расход за месяц/оценка), ни на что не влияет функционально. 0 = не
    /// показывать оценку (цена неизвестна/провайдер бесплатный). Пришла на смену
    /// <c>MonthlyQuota</c> (ADR-0005 §9, третья редакция — квота заменена вентилем
    /// IWebSearchValveService + бюджетом прогона SearchWarmupRun.MaxPaidCalls, оба явно
    /// заданы админом, а не завязаны на угадывание месячного лимита). Env:
    /// <c>Enrichment__PricePerPaidCall</c>.</summary>
    public decimal PricePerPaidCall { get; set; }
}

public enum MedicationSearchProviderKind { Null, Brave, Yandex }
