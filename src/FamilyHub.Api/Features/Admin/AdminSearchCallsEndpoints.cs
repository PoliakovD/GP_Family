using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Аудит платных вызовов внешнего веб-поиска (WebSearchCallLog, см. .claude/plans/
/// ethereal-hugging-chipmunk.md, часть 2) — для дебага "куда уходят деньги": полный текст
/// запроса, исход, длительность, доля кэш-хитов. Соседствует с /api/admin/enrichment
/// (домены/кэш) — тот же провайдер, та же квота, тот же аудит одного и того же внешнего вызова с
/// разных сторон (кэш — "что лежит", этот эндпоинт — "что реально произошло").
/// </summary>
public static class AdminSearchCallsEndpoints
{
    public static void MapAdminSearchCallsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/search-calls").RequireAuthorization("PlatformAdmin");

        group.MapGet("/", async (
            string? provider, WebSearchTopic? topic, WebSearchCallOutcome? outcome, string? query,
            DateTime? from, DateTime? to, int? page, int? pageSize,
            AppDbContext db, CancellationToken ct) =>
        {
            var pageSize2 = Math.Clamp(pageSize ?? 25, 1, 200);
            var page2 = Math.Max(page ?? 1, 1);

            var filtered = db.WebSearchCallLogs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(provider)) filtered = filtered.Where(l => l.Provider == provider);
            if (topic is not null) filtered = filtered.Where(l => l.Topic == topic);
            if (outcome is not null) filtered = filtered.Where(l => l.Outcome == outcome);
            if (!string.IsNullOrWhiteSpace(query)) filtered = filtered.Where(l => l.NormalizedName.Contains(query));
            if (from is not null) filtered = filtered.Where(l => l.OccurredAt >= from.Value);
            if (to is not null) filtered = filtered.Where(l => l.OccurredAt <= to.Value);

            var total = await filtered.CountAsync(ct);
            var rows = await filtered
                .OrderByDescending(l => l.OccurredAt)
                .Skip((page2 - 1) * pageSize2)
                .Take(pageSize2)
                .Select(l => new SearchCallRowDto(
                    l.Id, l.OccurredAt, l.Provider, l.Topic, l.NormalizedName, l.SpecimenDisplayName,
                    l.HttpStatus, l.DurationMs, l.Outcome, l.SnippetCount, l.JobKind, l.JobId))
                .ToListAsync(ct);

            return Results.Ok(new SearchCallListResponse(rows, total, page2, pageSize2));
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var log = await db.WebSearchCallLogs.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
            if (log is null) return Results.NotFound();

            var urls = string.IsNullOrEmpty(log.ResultUrlsJson)
                ? []
                : JsonSerializer.Deserialize<List<string>>(log.ResultUrlsJson) ?? [];

            return Results.Ok(new SearchCallDetailDto(
                log.Id, log.OccurredAt, log.Provider, log.Topic, log.NormalizedName, log.SpecimenDisplayName,
                log.QueryText, log.Endpoint, log.HttpStatus, log.DurationMs, log.Outcome, log.SnippetCount,
                urls, log.Error, log.JobKind, log.JobId));
        });

        // Период по умолчанию — последние 30 дней (from не задан) — тот же дефолт, что плитка
        // «Платных вызовов за 30 дней» на /admin/overview использует под капотом.
        group.MapGet("/stats", async (
            DateTime? from, DateTime? to, AppDbContext db, IOptions<EnrichmentOptions> enrichmentOptions, CancellationToken ct) =>
        {
            var fromDate = from ?? DateTime.UtcNow.AddDays(-30);
            var toDate = to ?? DateTime.UtcNow;

            // Материализуем период ОДНИМ запросом и считаем все разбивки в памяти: g.Key.ToString()
            // на enum-группировке не всегда транслируется в SQL Npgsql-провайдером, а период отчёта
            // (30-90 дней) достаточно мал, чтобы не упираться в это на каждой разбивке отдельно.
            var rows = await db.WebSearchCallLogs.AsNoTracking()
                .Where(l => l.OccurredAt >= fromDate && l.OccurredAt <= toDate)
                .Select(l => new { l.OccurredAt, l.Outcome, l.Provider })
                .ToListAsync(ct);

            var totalCalls = rows.Count;
            var cacheHits = rows.Count(r => r.Outcome == WebSearchCallOutcome.CacheHit);
            var paidCalls = totalCalls - cacheHits;

            var byProvider = rows
                .GroupBy(r => r.Provider)
                .Select(g => new SearchCallCountByKeyDto(g.Key, g.Count()))
                .ToList();

            var byOutcome = rows
                .GroupBy(r => r.Outcome)
                .Select(g => new SearchCallCountByKeyDto(g.Key.ToString(), g.Count()))
                .ToList();

            var byDay = rows
                .GroupBy(r => DateOnly.FromDateTime(r.OccurredAt))
                .OrderBy(g => g.Key)
                .Select(g => new SearchCallDailyCountDto(
                    g.Key,
                    g.Count(r => r.Outcome != WebSearchCallOutcome.CacheHit),
                    g.Count(r => r.Outcome == WebSearchCallOutcome.CacheHit)))
                .ToList();

            var monthStartUtc = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var usedThisMonth = await db.WebSearchCallLogs.AsNoTracking()
                .CountAsync(l => l.OccurredAt >= monthStartUtc && l.Outcome != WebSearchCallOutcome.CacheHit, ct);
            var monthlyQuota = enrichmentOptions.Value.MonthlyQuota;

            return Results.Ok(new SearchCallStatsDto(
                totalCalls, paidCalls, cacheHits, totalCalls == 0 ? 0 : (double)cacheHits / totalCalls,
                byProvider, byOutcome, byDay, usedThisMonth, monthlyQuota > 0 ? monthlyQuota : null));
        });
    }
}
