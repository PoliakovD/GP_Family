namespace FamilyHub.Modules.Medical.Ocr;

public static class MedicationOcrEndpoints
{
    public static void MapMedicationOcrEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        // Возвращает 200 даже при неудачном распознавании (Success=false + Error) — это
        // ожидаемый бизнес-исход ("модель не смогла разобрать фото"), а не ошибка сервера.
        group.MapPost("/medications/ocr", async (
            IFormFileCollection files, MedicationOcrService service, CancellationToken ct) =>
        {
            var response = await service.ExtractAsync(files, ct);
            return Results.Ok(response);
        }).DisableAntiforgery();
    }
}
