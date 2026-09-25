using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>Пинг LM Studio (GET /v1/models) — единственная реализация, общая для
/// <see cref="FamilyHub.Api.Health.LmStudioHealthCheck"/> (ручной /health/llm),
/// LmStudioRecoverySweepJob (рекуррентный досып ждущих задач), постановки распознавания и
/// пользовательского GET /api/ai/status (глобальная плашка «ИИ недоступен»).</summary>
public interface ILmStudioAvailabilityProbe
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>Singleton с коротким кэшем ответа: статус ИИ теперь читает каждый открытый клиент
/// раз в полминуты, а при недоступном сервере каждый пинг ждёт полный 3-секундный таймаут — без
/// кэша десяток вкладок мог бы держать пулы запросов на мёртвом хосте.</summary>
public class LmStudioAvailabilityProbe(IHttpClientFactory httpClientFactory, IOptions<LmStudioOptions> options)
    : ILmStudioAvailabilityProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private DateTime _checkedAt = DateTime.MinValue;
    private bool _available;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _checkedAt < CacheTtl) return _available;
        }

        var available = await PingAsync(ct);

        lock (_gate)
        {
            _available = available;
            _checkedAt = DateTime.UtcNow;
        }
        return available;
    }

    private async Task<bool> PingAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(options.Value.BaseUrl);
            using var response = await client.GetAsync("v1/models", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}
