using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Управление доверенными доменами и кэшем сырых результатов поиска обоих конвейеров обогащения
/// (пересборка enrich-пайплайна) — раньше список доменов был статикой в appsettings, а кэш хранил
/// только уже отфильтрованные сниппеты; теперь домены — в БД (EnrichmentTrustedDomain), а кэш
/// хранит ВСЕ сниппеты, так что список можно поменять и точечно переопределить отдельные URL, не
/// тратя новый платный запрос — следующий прогон обогащения того же названия сразу увидит новое
/// решение (см. EnrichmentSnippetFilter).
/// </summary>
public static class AdminEnrichmentEndpoints
{
    public static void MapAdminEnrichmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/enrichment").RequireAuthorization("PlatformAdmin");

        group.MapGet("/trusted-domains", async (
            WebSearchTopic topic, EnrichmentTrustedDomainService service, CancellationToken ct) =>
        {
            var domains = await service.GetAllAsync(topic, ct);
            return Results.Ok(domains.Select(d => new TrustedDomainDto(d.Id, d.Domain, d.Rank, d.IsEnabled)));
        });

        group.MapPost("/trusted-domains", async (
            AddTrustedDomainRequest request, EnrichmentTrustedDomainService service, CancellationToken ct) =>
        {
            var (success, domain) = await service.AddAsync(request.Topic, request.Domain, ct);
            return success
                ? Results.Created($"/api/admin/enrichment/trusted-domains/{domain!.Id}",
                    new TrustedDomainDto(domain.Id, domain.Domain, domain.Rank, domain.IsEnabled))
                : Results.Conflict(new { code = "duplicate_or_invalid", message = "Домен уже в списке или не распознан." });
        });

        group.MapPut("/trusted-domains/{id:guid}", async (
            Guid id, SetTrustedDomainEnabledRequest request, EnrichmentTrustedDomainService service, CancellationToken ct) =>
        {
            var updated = await service.SetEnabledAsync(id, request.IsEnabled, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        });

        group.MapDelete("/trusted-domains/{id:guid}", async (
            Guid id, EnrichmentTrustedDomainService service, CancellationToken ct) =>
        {
            var deleted = await service.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/trusted-domains/reorder", async (
            ReorderTrustedDomainsRequest request, EnrichmentTrustedDomainService service, CancellationToken ct) =>
        {
            await service.SetOrderAsync(request.Topic, request.OrderedIds, ct);
            return Results.NoContent();
        });

        group.MapGet("/search-cache", async (
            WebSearchTopic topic, string? query, int? skip, int? take,
            MedicationSearchCacheService medicationCache, LabAnalyteSearchCacheService analyteCache,
            AppDbContext db, CancellationToken ct) =>
        {
            var take2 = Math.Clamp(take ?? 25, 1, 100);
            var skip2 = Math.Max(skip ?? 0, 0);

            if (topic == WebSearchTopic.Medication)
            {
                var (rows, total) = await medicationCache.ListAsync(query, skip2, take2, ct);
                return Results.Ok(new SearchCacheListResponse(
                    rows.Select(r => new SearchCacheRowDto(
                        r.Id, r.NormalizedName, null, r.Provider, r.LastUpdatedAt, r.CanBeUpdatedAfter,
                        CountSnippets(r.SnippetsJson))).ToList(),
                    total));
            }
            else
            {
                var (rows, total) = await analyteCache.ListAsync(query, skip2, take2, ct);
                var specimenNames = await ResolveSpecimenNamesAsync(db, rows.Select(r => r.SpecimenKbId), ct);
                return Results.Ok(new SearchCacheListResponse(
                    rows.Select(r => new SearchCacheRowDto(
                        r.Id, r.NormalizedName, specimenNames.GetValueOrDefault(r.SpecimenKbId, r.SpecimenKbId.ToString()),
                        r.Provider, r.LastUpdatedAt, r.CanBeUpdatedAfter, CountSnippets(r.SnippetsJson))).ToList(),
                    total));
            }
        });

        group.MapGet("/search-cache/{id:guid}", async (
            Guid id, WebSearchTopic topic, MedicationSearchCacheService medicationCache,
            LabAnalyteSearchCacheService analyteCache, EnrichmentTrustedDomainService trustedDomains,
            AppDbContext db, CancellationToken ct) =>
        {
            var activeDomains = await trustedDomains.GetActiveDomainsByPriorityAsync(topic, ct);

            if (topic == WebSearchTopic.Medication)
            {
                var row = await medicationCache.GetByIdAsync(id, ct);
                if (row is null) return Results.NotFound();
                var cached = await medicationCache.GetCachedAsync(row.NormalizedName, ct);
                return Results.Ok(BuildDetail(id, row.NormalizedName, null, row.Provider, row.LastUpdatedAt,
                    row.CanBeUpdatedAfter, cached?.Snippets ?? [], cached?.Overrides, activeDomains));
            }
            else
            {
                var row = await analyteCache.GetByIdAsync(id, ct);
                if (row is null) return Results.NotFound();
                var cached = await analyteCache.GetCachedAsync(row.NormalizedName, row.SpecimenKbId, ct);
                var specimenName = await db.GlobalSpecimensKb.AsNoTracking()
                    .Where(s => s.Id == row.SpecimenKbId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct)
                    ?? row.SpecimenKbId.ToString();
                return Results.Ok(BuildDetail(id, row.NormalizedName, specimenName, row.Provider, row.LastUpdatedAt,
                    row.CanBeUpdatedAfter, cached?.Snippets ?? [], cached?.Overrides, activeDomains));
            }
        });

        // Кэш поиска показателей с нерезолвленным источником (SpecimenKbId=Unresolved) — чистый
        // мусор, наследие до пересборки enrich-пайплайна анализов (жёсткий гейт не даёт новым
        // задачам с таким источником ставиться в очередь, значит и кэш для них больше не читается
        // ни одной задачей). Только LabAnalyte — у медикаментов нет понятия "источник".
        group.MapPost("/search-cache/lab-analytes/purge-unresolved-specimen", async (
            LabAnalyteSearchCacheService analyteCache, CancellationToken ct) =>
        {
            var deleted = await analyteCache.PurgeUnresolvedSpecimenAsync(ct);
            return Results.Ok(new { deletedCount = deleted });
        });

        group.MapPost("/search-cache/{id:guid}/override", async (
            Guid id, SetSnippetOverrideRequest request, MedicationSearchCacheService medicationCache,
            LabAnalyteSearchCacheService analyteCache, CancellationToken ct) =>
        {
            var updated = request.Topic == WebSearchTopic.Medication
                ? await medicationCache.SetSnippetOverrideAsync(id, request.Url, request.Enabled, ct)
                : await analyteCache.SetSnippetOverrideAsync(id, request.Url, request.Enabled, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        });
    }

    private static SearchCacheDetailDto BuildDetail(
        Guid id, string normalizedName, string? specimen, string provider, DateTime lastUpdatedAt, DateTime canBeUpdatedAfter,
        IReadOnlyList<WebSnippet> snippets, IReadOnlyDictionary<string, bool>? overrides, IReadOnlyList<string> activeDomains)
    {
        var snippetDtos = snippets.Select(s =>
        {
            var domain = Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
            var isTrusted = EnrichmentSnippetFilter.IsTrustedDomain(s.Url, activeDomains);
            var hasOverride = overrides is not null && overrides.TryGetValue(s.Url, out var overrideValue);
            var enabled = hasOverride ? overrides![s.Url] : isTrusted;
            return new SearchCacheSnippetDto(s.Title, s.Url, s.Text, domain, isTrusted, hasOverride ? overrides![s.Url] : null, enabled);
        }).ToList();

        return new SearchCacheDetailDto(id, normalizedName, specimen, provider, lastUpdatedAt, canBeUpdatedAfter, snippetDtos);
    }

    /// <summary>Батч-резолв DisplayName источников (пересборка enrich-пайплайна: строки кэша
    /// хранят только SpecimenKbId, не текст) — один запрос на страницу списка, не N+1.</summary>
    private static async Task<Dictionary<Guid, string>> ResolveSpecimenNamesAsync(
        AppDbContext db, IEnumerable<Guid> specimenKbIds, CancellationToken ct)
    {
        var distinct = specimenKbIds.Distinct().ToList();
        if (distinct.Count == 0) return [];
        return await db.GlobalSpecimensKb.AsNoTracking()
            .Where(s => distinct.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
    }

    private static int CountSnippets(string? snippetsJson)
    {
        if (string.IsNullOrEmpty(snippetsJson)) return 0;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(snippetsJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }
}
