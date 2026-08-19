using System.Security.Cryptography;
using System.Text;
using FamilyHub.TelegramBot.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;

namespace FamilyHub.TelegramBot.Webhook;

public static class BotEndpoints
{
    public static void MapBotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/bot/webhook", async (
            HttpContext httpContext, TelegramUpdateHandler handler, IOptions<BotOptions> options, CancellationToken ct) =>
        {
            // Проверка подлинности источника — ПЕРВЫЙ шаг, до разбора тела и любой бизнес-логики
            // (тот же принцип, что у валидации Mini App initData в TelegramInitDataValidator на Api).
            if (!IsValidSecret(httpContext, options.Value.WebhookSecret))
                return Results.Unauthorized();

            var update = await httpContext.Request.ReadFromJsonAsync<Update>(Telegram.Bot.JsonBotAPI.Options, ct);
            if (update is not null)
                await handler.HandleAsync(update, ct);

            return Results.Ok();
        }).AllowAnonymous();
    }

    private static bool IsValidSecret(HttpContext httpContext, string configuredSecret)
    {
        if (string.IsNullOrEmpty(configuredSecret))
            return false; // секрет вебхука не сконфигурирован — отказываем, а не пропускаем

        var received = httpContext.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (string.IsNullOrEmpty(received))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(configuredSecret));
    }
}
