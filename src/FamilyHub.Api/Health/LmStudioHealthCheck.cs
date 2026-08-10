using FamilyHub.Infrastructure.LmStudio;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Health;

/// <summary>
/// Отдельный тег "llm", не "ready": LM Studio живёт на ноутбуке пользователя за WireGuard-туннелем
/// (см. деплой-план) — недоступность ожидаема (ноутбук в спящем режиме) и не должна валить общую
/// готовность контура. OCR/суммаризация и так деградируют грациозно (LmStudioJsonClient ловит
/// HttpRequestException/TaskCanceledException и возвращает Success=false, см. MedicationOcrEndpoints) —
/// этот чек только делает недоступность видимой в /health/llm, а не отражает поведение бизнес-пути.
/// </summary>
public class LmStudioHealthCheck(IHttpClientFactory httpClientFactory, IOptions<LmStudioOptions> options) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.BaseAddress = new Uri(options.Value.BaseUrl);
            using var response = await client.GetAsync("v1/models", cts.Token);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded($"LM Studio вернул {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Degraded, не Unhealthy — намеренно: недоступность ноутбука не должна выглядеть как
            // сбой контура. См. класс-комментарий.
            return HealthCheckResult.Degraded("Локальный сервер распознавания недоступен.", ex);
        }
    }
}
