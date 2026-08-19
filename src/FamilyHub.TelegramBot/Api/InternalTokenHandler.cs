using FamilyHub.TelegramBot.Configuration;
using Microsoft.Extensions.Options;

namespace FamilyHub.TelegramBot.Api;

/// <summary>Добавляет X-Internal-Token к каждому запросу к /internal/bot/* (см. InternalBotAuthFilter в Api).</summary>
public class InternalTokenHandler(IOptions<InternalApiOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Add("X-Internal-Token", options.Value.BotApiToken);
        return base.SendAsync(request, ct);
    }
}
