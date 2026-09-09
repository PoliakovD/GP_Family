using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>Пинг LM Studio (GET /v1/models) — единственная реализация, общая для
/// <see cref="FamilyHub.Api.Health.LmStudioHealthCheck"/> (ручной /health/llm) и
/// LmStudioRecoverySweepJob (рекуррентный досып упавших транзиентно задач, см. план часть 1.4):
/// раньше пинг жил только внутри health-check, второму потребителю неоткуда было его переиспользовать.</summary>
public interface ILmStudioAvailabilityProbe
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public class LmStudioAvailabilityProbe(IHttpClientFactory httpClientFactory, IOptions<LmStudioOptions> options)
    : ILmStudioAvailabilityProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
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
