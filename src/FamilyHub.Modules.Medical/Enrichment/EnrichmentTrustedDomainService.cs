using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Доверенные домены конвейеров обогащения — БД-backed (пересборка enrich-пайплайна), заменяет
/// прежние статические массивы EnrichmentOptions.TrustedDomains/AnalyteTrustedDomains: включение/
/// выключение и переупорядочивание приоритета доступны через админку без передеплоя (см.
/// AdminEnrichmentEndpoints). Читается на каждый прогон процессора обогащения — таблица маленькая
/// (десятки строк), лишний DB round-trip дешевле, чем IOptions-снапшот, который не увидел бы
/// изменение без рестарта процесса.
/// </summary>
public class EnrichmentTrustedDomainService(AppDbContext db)
{
    /// <summary>Только включённые, по приоритету — то, что реально используется процессорами
    /// (EnrichmentSnippetFilter/ReferenceRangeMerger).</summary>
    public async Task<List<string>> GetActiveDomainsByPriorityAsync(WebSearchTopic topic, CancellationToken ct = default) =>
        await db.EnrichmentTrustedDomains.AsNoTracking()
            .Where(d => d.Topic == topic && d.IsEnabled)
            .OrderBy(d => d.Rank)
            .Select(d => d.Domain)
            .ToListAsync(ct);

    /// <summary>Все домены темы (включая выключенные) — для админ-панели.</summary>
    public async Task<List<EnrichmentTrustedDomain>> GetAllAsync(WebSearchTopic topic, CancellationToken ct = default) =>
        await db.EnrichmentTrustedDomains.AsNoTracking()
            .Where(d => d.Topic == topic)
            .OrderBy(d => d.Rank)
            .ToListAsync(ct);

    public async Task<(bool Success, EnrichmentTrustedDomain? Domain)> AddAsync(
        WebSearchTopic topic, string rawDomain, CancellationToken ct = default)
    {
        var domain = NormalizeHost(rawDomain);
        if (domain.Length == 0) return (false, null);

        var exists = await db.EnrichmentTrustedDomains.AnyAsync(d => d.Topic == topic && d.Domain == domain, ct);
        if (exists) return (false, null);

        var maxRank = await db.EnrichmentTrustedDomains
            .Where(d => d.Topic == topic)
            .Select(d => (int?)d.Rank)
            .MaxAsync(ct) ?? -1;

        var entity = new EnrichmentTrustedDomain
        {
            Id = Guid.NewGuid(),
            Topic = topic,
            Domain = domain,
            Rank = maxRank + 1,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.EnrichmentTrustedDomains.Add(entity);
        await db.SaveChangesAsync(ct);
        return (true, entity);
    }

    public async Task<bool> SetEnabledAsync(Guid id, bool isEnabled, CancellationToken ct = default)
    {
        var affected = await db.EnrichmentTrustedDomains
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsEnabled, isEnabled), ct);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var affected = await db.EnrichmentTrustedDomains.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
        return affected > 0;
    }

    /// <summary>Переупорядочивает приоритет одним запросом с фронта — orderedIds задаёт полный
    /// новый порядок для темы (drag-and-drop в админке шлёт целиком). Только для LabAnalyte
    /// порядок реально на что-то влияет (ReferenceRangeMerger), но UI одинаков для обеих тем.</summary>
    public async Task SetOrderAsync(WebSearchTopic topic, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        var domains = await db.EnrichmentTrustedDomains.Where(d => d.Topic == topic).ToListAsync(ct);
        var byId = domains.ToDictionary(d => d.Id);

        for (var rank = 0; rank < orderedIds.Count; rank++)
        {
            if (byId.TryGetValue(orderedIds[rank], out var domain))
                domain.Rank = rank;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Хост без схемы/пути/www — те же правила, что раньше подразумевались статическим
    /// конфигом (например "invitro.ru", не "https://www.invitro.ru/").</summary>
    private static string NormalizeHost(string rawDomain)
    {
        var trimmed = rawDomain.Trim().ToLowerInvariant();
        if (trimmed.Length == 0) return string.Empty;

        // Пользователь мог вставить полный URL — вытаскиваем хост, а не заставляем вводить вручную.
        if (Uri.TryCreate(trimmed.Contains("://") ? trimmed : $"https://{trimmed}", UriKind.Absolute, out var uri))
            trimmed = uri.Host;

        return trimmed.StartsWith("www.") ? trimmed[4..] : trimmed;
    }
}
