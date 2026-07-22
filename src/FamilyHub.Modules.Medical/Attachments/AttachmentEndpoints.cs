using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;

namespace FamilyHub.Modules.Medical.Attachments;

public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

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
                _ => Results.Created($"/api/attachments/{item!.Id}", item),
            };
        }).DisableAntiforgery();

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
