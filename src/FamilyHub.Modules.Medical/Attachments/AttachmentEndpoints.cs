using FamilyHub.Infrastructure.CurrentUser;
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
    }
}
