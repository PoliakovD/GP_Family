using FamilyHub.Infrastructure.LmStudio;

namespace FamilyHub.Api.Features.Ai;

public static class AiStatusEndpoints
{
    /// <summary>Доступен ли сейчас локальный ИИ (LM Studio) — для глобальной плашки «ИИ временно
    /// недоступен, задачи ждут в очереди». Только флаг, без адресов/моделей (это видит любой
    /// авторизованный пользователь); ответ кэшируется в пробе на 10 с.</summary>
    public static void MapAiStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai/status", async (ILmStudioAvailabilityProbe probe, CancellationToken ct) =>
                Results.Ok(new AiStatusResponse(await probe.IsAvailableAsync(ct))))
            .RequireAuthorization();
    }
}

public record AiStatusResponse(bool Available);
