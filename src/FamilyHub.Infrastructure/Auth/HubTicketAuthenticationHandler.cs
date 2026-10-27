using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyHub.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.Auth;

/// <summary>
/// Схема <see cref="AuthSchemes.HubTicket"/> (ADR-0019 п. 3). Браузерный WebSocket не умеет слать заголовки,
/// поэтому SignalR-клиент кладёт токен в query <c>access_token</c>; negotiate и long polling — в
/// <c>Authorization: Bearer</c>. Тикет в логах маскируется (LogPathMasker).
/// </summary>
public class HubTicketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HubTicketService tickets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = Request.Query["access_token"].ToString();
        if (string.IsNullOrEmpty(ticket))
        {
            var header = Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.Ordinal)) ticket = header["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrEmpty(ticket)) return Task.FromResult(AuthenticateResult.NoResult());

        var userId = tickets.Validate(ticket);
        if (userId is null)
        {
            Logger.LogDebug("Тикет хаба отклонён: подпись не сошлась или срок истёк");
            return Task.FromResult(AuthenticateResult.Fail("Недействительный или просроченный тикет хаба."));
        }

        var identity = new ClaimsIdentity([new Claim(FamilyHubClaimTypes.UserId, userId.Value.ToString())], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
