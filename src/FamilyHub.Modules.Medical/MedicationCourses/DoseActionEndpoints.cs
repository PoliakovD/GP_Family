namespace FamilyHub.Modules.Medical.MedicationCourses;

public static class DoseActionEndpoints
{
    /// <summary>
    /// Кнопки push-уведомления («Принял», «Отложить», «Пропустить») — БЕЗ сессии и вне ConsentRequiredFilter:
    /// их вызывает service worker в фоне, а авторизует одноразовый токен приёма (ADR-0015). Это GET с
    /// побочным эффектом — осознанно: ngsw-операция <c>sendRequest</c> умеет только GET, URL нигде не
    /// показывается как ссылка, ответ <c>no-store</c>, повтор идемпотентен. Неверный/просроченный токен —
    /// 404, использованный для другого действия или уже неактуальный приём — 409; лимит по IP.
    /// </summary>
    public static void MapDoseActionPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/dose-actions").AllowAnonymous().RequireRateLimiting("dose-action");

        group.MapGet("/{token}", async (
            string token, string? a, DoseActionTokenService service, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var action = DoseReminderPushCustomizer.ParseAction(a);
            if (action is null) return Results.BadRequest();

            return await service.RedeemAsync(token, action.Value, ct) switch
            {
                DoseTokenResult.Success => Results.NoContent(),
                DoseTokenResult.Conflict => Results.Conflict(),
                _ => Results.NotFound(),
            };
        });
    }
}
