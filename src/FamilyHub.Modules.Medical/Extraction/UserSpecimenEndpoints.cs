using FamilyHub.Infrastructure.CurrentUser;

namespace FamilyHub.Modules.Medical.Extraction;

public record CreateSpecimenRequest(string Name);

public static class UserSpecimenEndpoints
{
    public static void MapUserSpecimenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/specimens").RequireAuthorization();

        group.MapGet("/", async (UserSpecimenService service, ICurrentUser currentUser, CancellationToken ct) =>
            Results.Ok(await service.GetOwnAsync(currentUser.UserId, ct)));

        // Автоподсказка по ВСЕМУ общему справочнику (не только "недавно использованные этим
        // пользователем" выше) — заменяет прежний захардкоженный select на 6 значений SpecimenType
        // (пересборка enrich-пайплайна): пользователь ищет и находит любой уже известный системе
        // источник, включая заведённые LLM при извлечении документов другими пользователями.
        group.MapGet("/search", async (
            string? q, int? take, GlobalSpecimenKbService globalKb, CancellationToken ct) =>
            Results.Ok(await globalKb.SearchAsync(q, take ?? 20, ct)));

        group.MapPost("/", async (
            CreateSpecimenRequest body, UserSpecimenService service, ICurrentUser currentUser, CancellationToken ct) =>
        {
            var (result, item, reason) = await service.CreateAsync(currentUser.UserId, body.Name, ct);
            return result switch
            {
                CreateSpecimenResult.Success => Results.Created($"/api/specimens/{item!.SpecimenKbId}", item),
                CreateSpecimenResult.AlreadyExists => Results.Ok(item),
                CreateSpecimenResult.InvalidInput => Results.BadRequest(new { code = "invalid_input", reason }),
                CreateSpecimenResult.Rejected => Results.UnprocessableEntity(new { code = "rejected", reason }),
                CreateSpecimenResult.Unavailable => Results.Json(
                    new { code = "unavailable", reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.BadRequest(),
            };
        });
    }
}
