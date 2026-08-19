using System.Security.Cryptography;
using System.Text;
using FamilyHub.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Bot;

/// <summary>
/// Гейт для /internal/bot/* — единственный клиент FamilyHub.TelegramBot подтверждает себя
/// заголовком X-Internal-Token. Constant-time сравнение и отказ при несконфигурированном
/// секрете — то же самое правило, что у BotEndpoints.IsValidSecret в боте (раньше — здесь же,
/// до выноса). Это не замена периметру: /internal/* дополнительно заблокирован в deploy/Caddyfile
/// и порт api:8080 наружу не публикуется вовсе — токен - вторая независимая линия защиты.
/// </summary>
public class InternalBotAuthFilter(IOptions<InternalOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configured = options.Value.BotApiToken;
        if (string.IsNullOrEmpty(configured))
            return Results.Unauthorized(); // секрет не сконфигурирован — отказываем, а не пропускаем

        var received = context.HttpContext.Request.Headers["X-Internal-Token"].ToString();
        if (string.IsNullOrEmpty(received)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(configured)))
            return Results.Unauthorized();

        return await next(context);
    }
}
