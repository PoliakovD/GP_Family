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

    /// <summary>Результат + момент пробы — одной парой за один атомарный обмен ссылки (аудит,
    /// находка Medium #8). Раньше это были два раздельных static-поля (_cachedAt/_cached):
    /// быстрый путь читал их вне семафора без volatile/барьера, запись внутри семафора шла
    /// не атомарно по паре — параллельные health-пробы теоретически могли увидеть
    /// рассинхронизированные значения (свежий _cachedAt со старым _cached, или наоборот).
    /// volatile-ссылка на неизменяемый record читается/пишется как единое целое.</summary>
    private sealed record CachedProbe(HealthCheckResult Result, DateTime CachedAt);

    private static volatile CachedProbe? _snapshot;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var bot = services.GetService<ITelegramBotClient>();
        if (bot is null)
            return HealthCheckResult.Healthy("Telegram:BotToken не задан — бот не сконфигурирован (локальный dev).");

        var snapshot = _snapshot;
        if (snapshot is not null && DateTime.UtcNow - snapshot.CachedAt < CacheTtl)
            return snapshot.Result;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && DateTime.UtcNow - snapshot.CachedAt < CacheTtl)
                return snapshot.Result;

            var result = await ProbeAsync(bot, cancellationToken);
            _snapshot = new CachedProbe(result, DateTime.UtcNow);
            return result;
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
