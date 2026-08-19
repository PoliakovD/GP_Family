using Microsoft.Extensions.Diagnostics.HealthChecks;
using Telegram.Bot;

namespace FamilyHub.TelegramBot.Health;

/// <summary>
/// Тег "telegram" — НЕ "ready": проверяет egress к api.telegram.org (GetMe) через сплит-туннель
/// Amnezia WG (см. deploy/docker-compose.prod.yml, network_mode: service:wg-client). Отдельный
/// тег намеренно: недоступность Telegram (туннель ещё не поднялся, холодный handshake) не должна
/// валить общую readiness бота — /health/ready проверяет только то, что бот способен принимать
/// вебхук и достучаться до Kafka/Api БЕЗ туннеля (см. Program.cs). Результат кэшируется на 60
/// секунд — иначе периодический опрос healthcheck'ом (docker-compose/деплой-гейт) дёргал бы
/// getMe чаще, чем разумно для внешнего API.
/// </summary>
public class TelegramApiHealthCheck(IServiceProvider services) : IHealthCheck
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static DateTime _cachedAt = DateTime.MinValue;
    private static HealthCheckResult _cached = HealthCheckResult.Unhealthy("Ещё не проверялось.");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var bot = services.GetService<ITelegramBotClient>();
        if (bot is null)
            return HealthCheckResult.Healthy("Telegram:BotToken не задан — бот не сконфигурирован (локальный dev).");

        if (DateTime.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            _cached = await ProbeAsync(bot, cancellationToken);
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<HealthCheckResult> ProbeAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await bot.GetMe(cts.Token);
            return HealthCheckResult.Healthy("getMe через туннель прошёл.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("getMe не прошёл — проверьте туннель Amnezia WG.", ex);
        }
    }
}
