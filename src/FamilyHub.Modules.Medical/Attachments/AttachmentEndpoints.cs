using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;

namespace FamilyHub.Modules.Medical.Attachments;

public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        // .DisableAntiforgery() ЗДЕСЬ ОБЯЗАТЕЛЕН, хотя раньше (до AddAntiforgery в Program.cs) был
        // no-op: с антифорджери, зарегистрированным в DI, ASP.NET Core сам примешивает требование
        // антифорджери-валидации к любому эндпоинту, принимающему IFormFile/IFormFileCollection —
        // а поскольку app.UseAntiforgery() мы намеренно НЕ подключаем (CSRF проверяется своим
        // глобальным гейтом в Program.cs, не встроенным middleware), без этого вызова запрос падал
        // бы в 500 ("required antiforgery middleware is not present"). Реальная CSRF-защита для
        // этого POST всё равно есть — тот самый глобальный гейт (IAntiforgery.IsRequestValidAsync
        // по заголовку X-XSRF-TOKEN, не трогает тело — безопасно для multipart-загрузки).
        group.MapPost("/medical-records/{recordId:guid}/attachments", async (
            Guid recordId, IFormFile file, AttachmentService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var (result, item) = await service.UploadForMedicalRecordAsync(
                recordId, currentUser.UserId, file.FileName, file.ContentType, file.Length, stream, ct);

            return result switch
            {
                AttachmentAccessResult.NotFound => Results.NotFound(),
                AttachmentAccessResult.Forbidden => Results.Forbid(),
                AttachmentAccessResult.TooLarge => Results.Json(
                    new { code = "attachment_too_large", maxSizeBytes = service.MaxSizeBytes },
                    statusCode: StatusCodes.Status413PayloadTooLarge),
                AttachmentAccessResult.UnsupportedContentType => Results.Json(
                    new { code = "unsupported_content_type", allowed = AttachmentService.AllowedContentTypes },
                    statusCode: StatusCodes.Status415UnsupportedMediaType),
                AttachmentAccessResult.TooManyFiles => Results.Json(
                    new { code = "attachment_limit_reached", maxFilesPerRecord = service.MaxFilesPerRecord },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Created($"/api/attachments/{item!.Id}", item),
            };
        }).DisableAntiforgery();

        // Лимиты — до попытки загрузки, чтобы фронт мог дизейблить кнопку/показать
        // «осталось N из 8» вместо того, чтобы узнавать о лимите только по факту отказа.
        group.MapGet("/attachments/limits", (AttachmentService service) => Results.Ok(service.Limits));

        group.MapGet("/medical-records/{recordId:guid}/attachments", async (
            Guid recordId, AttachmentService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetForMedicalRecordAsync(recordId, currentUser.UserId, ct);
            return result switch
            {
                AttachmentAccessResult.NotFound => Results.NotFound(),
                AttachmentAccessResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(items),
            };
        });

        group.MapGet("/attachments/{attachmentId:guid}/url", async (
            Guid attachmentId, AttachmentService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, url) = await service.GetPresignedUrlAsync(attachmentId, currentUser.UserId, ct);
            return result switch
            {
                AttachmentAccessResult.NotFound => Results.NotFound(),
                AttachmentAccessResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(new { url }),
            };
        });

        // Скачивание по подписанной короткоживущей ссылке (выдаётся эндпоинтом /url после
        // проверки доступа). AllowAnonymous: браузер открывает ссылку без auth-заголовков,
        // как раньше открывал presigned URL хранилища; защита — HMAC-подпись + TTL.
        app.MapGet("/api/attachments/{attachmentId:guid}/file", async (
            Guid attachmentId, long expires, string sig,
            AttachmentService service, DownloadTokenService tokens, CancellationToken ct) =>
        {
            if (!tokens.Validate(attachmentId, expires, sig))
                return Results.Unauthorized();

            var download = await service.GetDownloadAsync(attachmentId, ct);
            return download is null
                ? Results.NotFound()
                : Results.Stream(download.Value.Content, download.Value.ContentType, download.Value.FileName);
        }).AllowAnonymous();
    }
}
