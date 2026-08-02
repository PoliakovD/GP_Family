namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Конфигурация секции "Enrichment" в appsettings — внешний веб-поиск для обогащения справочника (этап 4).</summary>
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

    /// <summary>
    /// Домены, которым доверяем как медицинскому источнику РФ (ГРЛС/Vidal/RLS и т.п.) — сниппеты
    /// с других доменов отбрасываются провайдером ещё до похода к суммаризатору.
    /// </summary>
    public string[] TrustedDomains { get; set; } = ["grls.rosminzdrav.ru", "vidal.ru", "rlsnet.ru"];

    /// <summary>Месячный лимит внешних запросов (напр. free-tier Brave — 2000/мес; для Yandex —
    /// зависит от тарифа) — считается по Postgres, не в памяти.</summary>
    public int MonthlyQuota { get; set; } = 2000;

    /// <summary>Минимальный интервал между обращениями к платному API за ОДНО и то же нормализованное
    /// название (см. MedicationSearchCache) — защита от повторной оплаты за один и тот же препарат,
    /// не связана с MonthlyQuota (тот лимит — суммарный по всем названиям сразу).</summary>
    public int MinRefreshIntervalMonths { get; set; } = 1;

    /// <summary>Сколько сниппетов максимум передаём суммаризатору за один запрос.</summary>
    public int MaxSnippets { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 20;
}

public enum MedicationSearchProviderKind { Null, Brave, Yandex }
