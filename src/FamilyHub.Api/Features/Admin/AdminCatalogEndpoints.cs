using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Ручная правка справочников после ИИ из админки (§3 плана) — показатели, медикаменты,
/// источники. Каждое поле, присланное в PUT-теле показателя/медикамента, автоматически лочится
/// (AdminCatalogService) — следующее автообогащение его не тронет. Поиск/листинг переиспользует
/// существующие публичные сервисы (KbAnalyteCatalogService/KbCatalogService/GlobalSpecimenKbService) —
/// та же выдача, что видит обычный пользователь в разделе «Справочник», только с добавленными
/// кнопками правки.
/// </summary>
public static class AdminCatalogEndpoints
{
    public static void MapAdminCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/kb").RequireAuthorization("PlatformAdmin");

        // --- Показатели ---

        group.MapGet("/lab-analytes", async (
            string? q, int? skip, int? take, KbAnalyteCatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.SearchAsync(q, skip ?? 0, take ?? 20, ct)));

        group.MapGet("/lab-analytes/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
        {
            var detail = await admin.GetLabAnalyteAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/lab-analytes/{id:guid}", async (
            Guid id, AdminKbEditRequest request, AdminCatalogService admin, CancellationToken ct) =>
        {
            var (result, detail, reason) = await admin.UpdateLabAnalyteAsync(id, request, ct);
            return result switch
            {
                AdminKbEditResult.Ok => Results.Ok(detail),
                AdminKbEditResult.InvalidPayloadJson => Results.BadRequest(new { message = "PayloadJson — невалидный JSON." }),
                AdminKbEditResult.IsolationViolation => Results.BadRequest(new { message = $"Подозрение на персональный контекст: {reason}" }),
                _ => Results.NotFound(),
            };
        });

        group.MapDelete("/lab-analytes/{id:guid}/locks/{field}", async (
            Guid id, string field, AdminCatalogService admin, CancellationToken ct) =>
            await admin.UnlockLabAnalyteFieldAsync(id, field, ct) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/lab-analytes/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
            await admin.DeleteLabAnalyteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // --- Медикаменты ---

        group.MapGet("/medications", async (
            string? q, int? skip, int? take, KbCatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.SearchAsync(q, skip ?? 0, take ?? 20, ct)));

        group.MapGet("/medications/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
        {
            var detail = await admin.GetMedicationAsync(id, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapPut("/medications/{id:guid}", async (
            Guid id, AdminKbEditRequest request, AdminCatalogService admin, CancellationToken ct) =>
        {
            var (result, detail, reason) = await admin.UpdateMedicationAsync(id, request, ct);
            return result switch
            {
                AdminKbEditResult.Ok => Results.Ok(detail),
                AdminKbEditResult.InvalidPayloadJson => Results.BadRequest(new { message = "PayloadJson — невалидный JSON." }),
                AdminKbEditResult.IsolationViolation => Results.BadRequest(new { message = $"Подозрение на персональный контекст: {reason}" }),
                _ => Results.NotFound(),
            };
        });

        group.MapDelete("/medications/{id:guid}/locks/{field}", async (
            Guid id, string field, AdminCatalogService admin, CancellationToken ct) =>
            await admin.UnlockMedicationFieldAsync(id, field, ct) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/medications/{id:guid}", async (Guid id, AdminCatalogService admin, CancellationToken ct) =>
            await admin.DeleteMedicationAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // --- Источники показателей ---

        group.MapGet("/specimens", async (string? q, int? take, GlobalSpecimenKbService specimens, CancellationToken ct) =>
            Results.Ok(await specimens.SearchAsync(q, take ?? 20, ct)));

        group.MapPut("/specimens/{id:guid}", async (
            Guid id, AdminSpecimenRenameRequest request, GlobalSpecimenKbService specimens, CancellationToken ct) =>
        {
            var result = await specimens.RenameAsync(id, request.DisplayName, ct);
            return result switch
            {
                SpecimenRenameResult.Ok => Results.NoContent(),
                SpecimenRenameResult.Conflict => Results.Json(
                    new { code = "duplicate_or_invalid", message = "Такое название уже есть в справочнике источников." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });

        group.MapDelete("/specimens/{id:guid}", async (Guid id, GlobalSpecimenKbService specimens, CancellationToken ct) =>
        {
            var result = await specimens.DeleteAsync(id, ct);
            return result switch
            {
                SpecimenDeleteResult.Ok => Results.NoContent(),
                SpecimenDeleteResult.InUse => Results.Json(
                    new { code = "in_use", message = "Источник используется хотя бы одним показателем/статьёй справочника — сначала перепривяжите их." },
                    statusCode: StatusCodes.Status409Conflict),
                SpecimenDeleteResult.Sentinel => Results.Json(
                    new { code = "sentinel", message = "Системная запись «источник не определён» не удаляется." },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.NotFound(),
            };
        });
    }
}
