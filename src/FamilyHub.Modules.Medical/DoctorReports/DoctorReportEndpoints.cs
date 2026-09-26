using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.DoctorReports;

public static class DoctorReportEndpoints
{
    /// <summary>Эндпоинты владельца — внутри группы модуля (ConsentRequiredFilter): отчёт — обработка медданных.</summary>
    public static void MapDoctorReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/doctor-reports").RequireAuthorization();

        group.MapGet("", async (DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(currentUser.UserId, ct)));

        // Счётчик под выбором периода в форме: «4 анализа, 2 приёма, 38 записей дневника».
        group.MapGet("/preview", async (
            DateOnly from, DateOnly to, DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, counts, error) = await service.PreviewAsync(currentUser.UserId, from, to, ct);
            return result == DoctorReportResult.Invalid ? Results.BadRequest(new { message = error }) : Results.Ok(counts);
        });

        // Сборка PDF — тяжёлая (Chromium в Gotenberg): тот же лимит на пользователя, что у записи медданных.
        group.MapPost("", async (
            CreateDoctorReportRequest request, DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.CreateAsync(currentUser.UserId, request, ct);
            return result switch
            {
                DoctorReportResult.Invalid => Results.BadRequest(new { message = error }),
                DoctorReportResult.NoData => Results.UnprocessableEntity(new { message = error }),
                DoctorReportResult.TooMany => Results.Conflict(new { message = error }),
                DoctorReportResult.PdfUnavailable => Results.Json(new { message = error }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Created($"/api/doctor-reports/{item!.Id}", item),
            };
        }).RequireRateLimiting("medical-write");

        group.MapGet("/{reportId:guid}/pdf", async (
            Guid reportId, DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var pdf = await service.OpenOwnerPdfAsync(currentUser.UserId, reportId, ct);
            return pdf is null ? Results.NotFound() : Results.File(pdf.Value.Content, "application/pdf", pdf.Value.FileName);
        });

        group.MapPost("/{reportId:guid}/share", async (
            Guid reportId, ShareDoctorReportRequest request, DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, error) = await service.ShareAsync(currentUser.UserId, reportId, request.Days, ct);
            return result switch
            {
                DoctorReportResult.NotFound => Results.NotFound(),
                DoctorReportResult.Invalid => Results.BadRequest(new { message = error }),
                _ => Results.Ok(item),
            };
        });

        group.MapPost("/{reportId:guid}/revoke", async (
            Guid reportId, DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.RevokeAsync(currentUser.UserId, reportId, ct);
            return result == DoctorReportResult.NotFound ? Results.NotFound() : Results.Ok(item);
        });

        group.MapDelete("/{reportId:guid}", async (
            Guid reportId, DoctorReportService service, ICurrentUser currentUser, CancellationToken ct) =>
            await service.DeleteAsync(currentUser.UserId, reportId, ct) == DoctorReportResult.NotFound
                ? Results.NotFound()
                : Results.NoContent());
    }

    /// <summary>
    /// Публичный доступ врача по ссылке — БЕЗ аккаунта и вне ConsentRequiredFilter (смотрящий — не пользователь
    /// приложения; согласие на обработку дал пациент, выдав ссылку). Единственное исключение из правила
    /// «файлы только по короткоживущим подписанным ссылкам»: токен в БД (хеш), срок ≤ 30 дней, отзыв, аудит
    /// каждого открытия, лимит по IP. Неверный/истёкший/отозванный токен — один и тот же 404.
    /// </summary>
    public static void MapDoctorReportPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/doctor-reports").AllowAnonymous().RequireRateLimiting("public-report");

        group.MapGet("/{token}", async (string token, DoctorReportService service, CancellationToken ct) =>
        {
            var meta = await service.GetPublicMetaAsync(token, ct);
            return meta is null ? Results.NotFound() : Results.Ok(meta);
        });

        // Без имени файла — disposition inline: фронт показывает PDF во вьюере, а скачивание делает из того же blob.
        group.MapGet("/{token}/pdf", async (string token, DoctorReportService service, CancellationToken ct) =>
        {
            var pdf = await service.OpenPublicPdfAsync(token, ct);
            return pdf is null ? Results.NotFound() : Results.File(pdf.Value.Content, "application/pdf");
        });
    }
}
