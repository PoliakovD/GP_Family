using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Api.Features.Consents;

public record AcceptConsentRequest(string Version);

public static class ConsentEndpoints
{
    public static void MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/consents");

        // Текст и версия согласия нужны ДО аутентификации/принятия — анонимно.
        group.MapGet("/current", (ConsentService service) => Results.Ok(new
        {
            version = service.CurrentVersion,
            text = ConsentService.LoadLegalText("pdn-consent.html"),
        })).AllowAnonymous();

        group.MapGet("/status", async (ConsentService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(new
            {
                accepted = await service.HasAcceptedCurrentAsync(currentUser.UserId, ct),
                version = service.CurrentVersion,
            }));

        group.MapPost("/accept", async (
            AcceptConsentRequest request, ConsentService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.AcceptAsync(currentUser.UserId, request.Version, ct);
            return result == AcceptConsentResult.StaleVersion
                ? Results.BadRequest(new { code = "stale_version", currentVersion = service.CurrentVersion })
                : Results.NoContent();
        });

        // Политика конфиденциальности — публичная страница.
        app.MapGet("/api/legal/privacy-policy", () =>
                Results.Content(ConsentService.LoadLegalText("privacy-policy.html"), "text/html; charset=utf-8"))
            .AllowAnonymous();
    }
}
