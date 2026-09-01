using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.MedicalRecords;

public record ShareFamilyRequest(Guid FamilyId);

public static class MedicalRecordEndpoints
{
    public static void MapMedicalRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/medical-records").RequireAuthorization();

        // kind опционален: без него отдаются оба вида (обратная совместимость со старыми клиентами).
        // UX-редизайн: серверные фильтры + пагинация (дефолт 15/стр.) вместо голого списка.
        group.MapGet("/", async (
            string? kind, DateOnly? from, DateOnly? to, Guid? dependentId, Guid? targetUserId, bool? self,
            string? doctor, string? q, int? page, int? pageSize,
            MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var filter = new MedicalRecordFilter(
                ParseKind(kind), from, to, dependentId, targetUserId, self ?? false, doctor, q,
                page ?? 1, pageSize ?? 15);
            return Results.Ok(await service.GetVisibleRecordsAsync(currentUser.UserId, filter, ct));
        });

        // L1-семьи текущего пользователя (владельца) — нужны клиенту, чтобы отрисовать состояние
        // тумблеров доступа в bottom-sheet «Доступ», не запрашивая его отдельно на каждую запись.
        group.MapGet("/shares", async (MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetSharedFamilyIdsAsync(currentUser.UserId, ct)));

        // Автоподсказка «Врач» в форме создания (v2) — только доктора, которых пользователь уже
        // вводил в СВОИХ записях.
        group.MapGet("/doctors", async (MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetDoctorSuggestionsAsync(currentUser.UserId, ct)));

        group.MapPost("/", async (
            CreateMedicalRecordRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, created) = await service.CreateAsync(currentUser.UserId, request, ct);
            return result switch
            {
                MedicalRecordAccessResult.NotFound => Results.NotFound(),
                MedicalRecordAccessResult.Forbidden => Results.Forbid(),
                MedicalRecordAccessResult.InvalidTarget => Results.BadRequest(new { code = "invalid_target" }),
                _ => Results.Created($"/api/medical-records/{created!.Id}", created),
            };
        });

        // Одна запись по id (редизайн v3) — мобильный экран открытой записи, deep link/refresh
        // без предзагрузки всего списка. Видимость, не владение — см. GetByIdAsync.
        group.MapGet("/{recordId:guid}", async (
            Guid recordId, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item) = await service.GetByIdAsync(currentUser.UserId, recordId, ct);
            return result switch
            {
                MedicalRecordAccessResult.NotFound => Results.NotFound(),
                MedicalRecordAccessResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(item),
            };
        });

        // Правка даты/врача/описания (UX-редизайн) — только владелец.
        group.MapPut("/{recordId:guid}", async (
            Guid recordId, UpdateMedicalRecordRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, updated) = await service.UpdateAsync(currentUser.UserId, recordId, request, ct);
            return result switch
            {
                MedicalRecordAccessResult.NotFound => Results.NotFound(),
                MedicalRecordAccessResult.Forbidden => Results.Forbid(),
                _ => Results.Ok(updated),
            };
        });

        // Безусловное удаление — только владелец (кто физически загрузил), см.
        // MedicalRecordService.DeleteAsync.
        group.MapDelete("/{recordId:guid}", async (
            Guid recordId, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(currentUser.UserId, recordId, ct);
            return MapResult(result);
        });

        group.MapPost("/share", async (
            ShareFamilyRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.ShareWithFamilyAsync(currentUser.UserId, request.FamilyId, ct);
            return result == MedicalRecordAccessResult.Forbidden ? Results.Forbid() : Results.NoContent();
        });

        group.MapPost("/unshare", async (
            ShareFamilyRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UnshareFamilyAsync(currentUser.UserId, request.FamilyId, ct);
            return result == MedicalRecordAccessResult.NotFound ? Results.NotFound() : Results.NoContent();
        });

        group.MapPost("/{recordId:guid}/hide", async (
            Guid recordId, FamilyIdsRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.HideFromFamiliesAsync(currentUser.UserId, recordId, request.FamilyIds, ct);
            return MapResult(result);
        });

        group.MapPost("/{recordId:guid}/unhide", async (
            Guid recordId, FamilyIdsRequest request, MedicalRecordService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var result = await service.UnhideFromFamiliesAsync(currentUser.UserId, recordId, request.FamilyIds, ct);
            return MapResult(result);
        });
    }

    private static IResult MapResult(MedicalRecordAccessResult result) => result switch
    {
        MedicalRecordAccessResult.NotFound => Results.NotFound(),
        MedicalRecordAccessResult.Forbidden => Results.Forbid(),
        _ => Results.NoContent(),
    };

    // "visit" на проводе короче и совпадает с токеном /api/search?types=visit (SearchDtos) и
    // фронтовым сегментом роута /health/visits — сам enum в коде называется DoctorVisit
    // (детальнее описывает сущность), поэтому это не Enum.TryParse, а явное сопоставление.
    private static MedicalRecordKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "analysis" => MedicalRecordKind.Analysis,
        "visit" => MedicalRecordKind.DoctorVisit,
        _ => null,
    };
}
