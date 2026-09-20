namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>Интерфейс над <see cref="WebSearchValveService"/> — та же причина, что у
/// <see cref="IPipelineConfigService"/>/<c>IPromptProvider</c> в модуле: юнит-тесты процессоров
/// подставляют сюда заглушку "открыто" вместо поднятия настоящего AppDbContext.</summary>
public interface IWebSearchValveService
{
    /// <summary>true — платный веб-поиск на паузе, платную ветку конвейера нужно отложить
    /// (см. EnrichmentJobStatus.Deferred). Читается БЕЗ кеша — см. class doc реализации.</summary>
    Task<bool> IsPausedAsync(CancellationToken ct = default);

    Task SetPausedAsync(bool paused, string? note, CancellationToken ct = default);
}
