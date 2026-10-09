using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;
using FamilyHub.Modules.Medical.Attachments;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>Файлы прививок: скан на конкретную дозу и «фото сертификата» (распознавание + подтверждение).</summary>
public static class VaccinationFileEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapVaccinationFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vaccinations").RequireAuthorization();

        // Скан на конкретную дозу (не сертификат целиком) — прикрепляется прямо к записи.
        group.MapPost("/{id:guid}/attachments", async (
            Guid id, IFormFile file, VaccinationAccess access, AttachmentService attachments,
            ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (item, level, _) = await access.LoadAsync(currentUser.UserId, id, tracking: false, ct);
            if (item is null) return Results.NotFound();
            if (level != VaccinationAccessLevel.Full) return Results.StatusCode(StatusCodes.Status403Forbidden);

            await using var stream = file.OpenReadStream();
            // Тот же размер/allow-list/сигнатура, что у вложений мед-записей (раньше здесь не было
            // никаких ограничений — аудит security-audit-2026-10).
            switch (attachments.ValidateUpload(file.ContentType, file.Length, stream))
            {
                case AttachmentAccessResult.TooLarge:
                    return Results.Json(new { code = "attachment_too_large", maxSizeBytes = attachments.MaxSizeBytes },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                case AttachmentAccessResult.UnsupportedContentType:
                    return Results.Json(new { code = "unsupported_content_type", allowed = AttachmentService.AllowedContentTypes },
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            var dto = await attachments.UploadRawAsync(
                FileOwnerType.Vaccination, id, file.FileName, file.ContentType, file.Length, stream, ct);
            return Results.Created($"/api/attachments/{dto.Id}", dto);
        }).DisableAntiforgery().RequireRateLimiting("medical-write");

        group.MapGet("/{id:guid}/attachments", async (
            Guid id, VaccinationAccess access, AttachmentService attachments, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (item, level, _) = await access.LoadAsync(currentUser.UserId, id, tracking: false, ct);
            if (item is null || level == VaccinationAccessLevel.None) return Results.NotFound();
            return Results.Ok(await attachments.ListRawAsync(FileOwnerType.Vaccination, id, ct));
        });

        // Фото сертификата — распознавание (без сохранения) и подтверждение (сохраняет фото + прививки).
        group.MapPost("/certificate/recognize", async (
            IFormFileCollection files, VaccinationCertificateOcrService service, CancellationToken ct) =>
            Results.Ok(await service.RecognizeAsync(files, ct))).DisableAntiforgery().RequireRateLimiting("llm");

        group.MapPost("/certificate/confirm", async (
            HttpRequest request, VaccinationCertificateService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { message = "Ожидается multipart/form-data." });
            var form = await request.ReadFormAsync(ct);

            var subjectKind = form["subjectKind"].ToString();
            if (!Guid.TryParse(form["subjectId"], out var subjectId))
                return Results.BadRequest(new { message = "Некорректный получатель." });

            List<BulkMarkItem>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<BulkMarkItem>>(form["items"].ToString(), Json);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "Некорректный список прививок." });
            }
            if (items is null) return Results.BadRequest(new { message = "Некорректный список прививок." });

            var (result, item, error) = await service.ConfirmAsync(currentUser.UserId, subjectKind, subjectId, form.Files, items, ct);
            return result switch
            {
                VaccinationResult.NotFound => Results.NotFound(),
                VaccinationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                VaccinationResult.Invalid => Results.BadRequest(new { message = error }),
                _ => Results.Ok(item),
            };
        }).DisableAntiforgery().RequireRateLimiting("medical-write");

        group.MapGet("/certificates/{certificateId:guid}/attachments", async (
            Guid certificateId, VaccinationCertificateService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, items) = await service.GetAttachmentsAsync(currentUser.UserId, certificateId, ct);
            return result == VaccinationResult.NotFound ? Results.NotFound() : Results.Ok(items);
        });

        // «Сертификат PDF» — снимок на лету, без хранения. Фронт качает через HttpClient (не
        // навигацией: Telegram-режим не приложит заголовок авторизации к обычной ссылке).
        group.MapGet("/people/{kind}/{id:guid}/certificate.pdf", async (
            string kind, Guid id, VaccinationCertificateExportService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, pdf, fileName) = await service.ExportAsync(currentUser.UserId, kind, id, ct);
            return result switch
            {
                VaccinationResult.NotFound => Results.NotFound(),
                VaccinationResult.PdfUnavailable => Results.Json(
                    new { message = "Не удалось сформировать PDF: сервис документов временно недоступен." },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.File(pdf!, "application/pdf", fileName),
            };
        });
    }
}
