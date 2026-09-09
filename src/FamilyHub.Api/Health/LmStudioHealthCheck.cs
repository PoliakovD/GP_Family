using FamilyHub.Infrastructure.LmStudio;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FamilyHub.Api.Health;

/// <summary>
/// Отдельный тег "llm", не "ready": LM Studio живёт на ноутбуке пользователя за WireGuard-туннелем
/// (см. деплой-план) — недоступность ожидаема (ноутбук в спящем режиме) и не должна валить общую
/// готовность контура. OCR/суммаризация и так деградируют грациозно (LmStudioJsonClient ловит
/// HttpRequestException/TaskCanceledException и возвращает Success=false, см. MedicationOcrEndpoints) —
/// этот чек только делает недоступность видимой в /health/llm, а не отражает поведение бизнес-пути.
/// Сам пинг — в <see cref="ILmStudioAvailabilityProbe"/> (общая реализация с LmStudioRecoverySweepJob).
/// </summary>
public class LmStudioHealthCheck(ILmStudioAvailabilityProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Degraded, не Unhealthy — намеренно: недоступность ноутбука не должна выглядеть как
        // сбой контура. См. класс-комментарий.
        return await probe.IsAvailableAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Локальный сервер распознавания недоступен.");
    }
}
