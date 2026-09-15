using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Один аудит-лог одного вызова SearchAsync (см. WebSearchCallLog для схемы/причины
/// каждого поля). Endpoint/HttpStatus/DurationMs/ResultUrlsJson — null там, где неприменимы
/// (CacheHit не делал HTTP-запрос вовсе).</summary>
public record WebSearchCallLogEntry(
    string Provider, WebSearchTopic Topic, string NormalizedName, string? SpecimenDisplayName,
    string QueryText, string? Endpoint, int? HttpStatus, int DurationMs, WebSearchCallOutcome Outcome,
    int SnippetCount, IReadOnlyList<string>? ResultUrls, string? Error, string? JobKind, Guid? JobId);

/// <summary>
/// Пишет WebSearchCallLog в СОБСТВЕННОМ DI-скоупе (IServiceScopeFactory → свой AppDbContext), не
/// в скоупный AppDbContext вызывающей стороны. Это не перестраховка: YandexSearchProvider/
/// BraveSearchProvider вызываются из середины *EnrichmentProcessor.RunAsync, где в скоупном
/// AppDbContext уже лежат незакоммиченные изменения задачи (job.Stage, job.ExternalSearchAt и
/// т.п.) — SaveChangesAsync на общем контексте отсюда незаметно закоммитил бы их раньше времени,
/// до того как процессор дойдёт до собственного финального SaveChangesAsync (а на пути между
/// записью лога и тем финалом ещё может случиться отказ гейта/суммаризатора).
///
/// Запись обёрнута в try/catch с логированием — аудит никогда не должен ронять сам поиск (тот же
/// принцип, что у LlmThinkingReportService).
/// </summary>
public class WebSearchCallLogger(IServiceScopeFactory scopeFactory, ILogger<WebSearchCallLogger> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task LogAsync(WebSearchCallLogEntry entry, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.WebSearchCallLogs.Add(new WebSearchCallLog
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTime.UtcNow,
                Provider = entry.Provider,
                Topic = entry.Topic,
                NormalizedName = entry.NormalizedName,
                SpecimenDisplayName = entry.SpecimenDisplayName,
                QueryText = Truncate(entry.QueryText, 4000),
                Endpoint = entry.Endpoint,
                HttpStatus = entry.HttpStatus,
                DurationMs = entry.DurationMs,
                Outcome = entry.Outcome,
                SnippetCount = entry.SnippetCount,
                ResultUrlsJson = entry.ResultUrls is { Count: > 0 } ? JsonSerializer.Serialize(entry.ResultUrls, JsonOptions) : null,
                Error = entry.Error is null ? null : Truncate(entry.Error, 2000),
                JobKind = entry.JobKind,
                JobId = entry.JobId,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Аудит — не критический путь: сбой записи лога не должен ронять реальный поиск/
            // суммаризацию, уже выполненные вызывающей стороной.
            logger.LogWarning(ex, "Не удалось записать WebSearchCallLog для «{Name}» ({Provider}/{Topic})",
                entry.NormalizedName, entry.Provider, entry.Topic);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
