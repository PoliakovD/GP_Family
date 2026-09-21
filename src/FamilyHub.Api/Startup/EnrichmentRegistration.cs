using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — внешний веб-поиск для обогащения
/// справочника препаратов/показателей (этап 4, ADR-0005), без изменения поведения/порядка.
/// </summary>
public static class EnrichmentRegistration
{
    public static WebApplicationBuilder AddFamilyHubEnrichment(this WebApplicationBuilder builder)
    {
        // Переключатель Enrichment:Provider = Null|Brave|Yandex (тот же паттерн конфиг-переключателя,
        // что раньше был у FileStorage:Provider, пока хранилище не свели к единственной реализации).
        // Без явного конфига — Null: наружу не уходит ни одного запроса (см. NullMedicationSearchProvider).
        // Аудит платных вызовов (часть 2 плана) — свой DI-скоуп (IServiceScopeFactory), регистрация не
        // зависит от выбранного провайдера: NullMedicationSearchProvider просто не вызывает LogAsync.
        builder.Services.AddSingleton<WebSearchCallLogger>();
        builder.Services.AddScoped<IWebSearchValveService, WebSearchValveService>();
        var enrichmentOptions = builder.Configuration.GetSection(EnrichmentOptions.SectionName).Get<EnrichmentOptions>()
            ?? new EnrichmentOptions();
        if (enrichmentOptions.Provider != MedicationSearchProviderKind.Null && string.IsNullOrWhiteSpace(enrichmentOptions.ApiKey))
        {
            throw new InvalidOperationException(
                $"Enrichment:Provider={enrichmentOptions.Provider} задан, но Enrichment:ApiKey (env Enrichment__ApiKey) пуст.");
        }
        if (enrichmentOptions.Provider == MedicationSearchProviderKind.Yandex && string.IsNullOrWhiteSpace(enrichmentOptions.FolderId))
        {
            throw new InvalidOperationException(
                "Enrichment:Provider=Yandex задан, но Enrichment:FolderId (env Enrichment__FolderId) пуст — " +
                "обязателен для Yandex Web Search API v2/gen/search.");
        }
        switch (enrichmentOptions.Provider)
        {
            case MedicationSearchProviderKind.Brave:
                builder.Services.AddHttpClient<IMedicationSearchProvider, BraveSearchProvider>((sp, client) =>
                {
                    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EnrichmentOptions>>().Value;
                    client.BaseAddress = new Uri("https://api.search.brave.com/");
                    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                });
                break;
            case MedicationSearchProviderKind.Yandex:
                builder.Services.AddHttpClient<IMedicationSearchProvider, YandexSearchProvider>((sp, client) =>
                {
                    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EnrichmentOptions>>().Value;
                    client.BaseAddress = new Uri("https://searchapi.api.cloud.yandex.net/");
                    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                });
                break;
            default:
                builder.Services.AddScoped<IMedicationSearchProvider, NullMedicationSearchProvider>();
                break;
        }

        return builder;
    }
}
