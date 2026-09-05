using System.Net.Http.Json;
using System.Text.Json.Serialization;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Features.Admin;

/// <summary>
/// Выбор активной модели LM Studio из админки — тот же приём, что PipelinePromptVersion: активное
/// значение в БД (LmStudioModelConfig), фолбэк на LmStudioOptions.Model (appsettings/env), если
/// админ ничего не выбрал (см. ILmStudioModelProvider). /available-models ходит на /v1/models
/// LM Studio тем же способом, что LmStudioHealthCheck, но здесь разбирается список id моделей, а
/// не только код ответа — недоступность LM Studio (ноутбук в спящем режиме) не 5xx, а пустой
/// список с LmStudioReachable=false, UI решает сам, что показать.
/// </summary>
public static class AdminLmStudioEndpoints
{
    public static void MapAdminLmStudioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/lmstudio").RequireAuthorization("PlatformAdmin");

        group.MapGet("/model", async (AppDbContext db, IOptions<LmStudioOptions> options, CancellationToken ct) =>
        {
            var configured = await db.LmStudioModelConfigs.AsNoTracking()
                .Select(c => c.ModelId)
                .FirstOrDefaultAsync(ct);
            return Results.Ok(new LmStudioModelResponse(configured, options.Value.Model));
        });

        group.MapGet("/available-models", async (
            IHttpClientFactory httpClientFactory, IOptions<LmStudioOptions> options, CancellationToken ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var client = httpClientFactory.CreateClient();
                client.BaseAddress = new Uri(options.Value.BaseUrl);
                using var response = await client.GetAsync("v1/models", cts.Token);
                if (!response.IsSuccessStatusCode)
                    return Results.Ok(new LmStudioAvailableModelsResponse([], false));

                var parsed = await response.Content.ReadFromJsonAsync<ModelsListResponse>(cancellationToken: cts.Token);
                var ids = (parsed?.Data ?? [])
                    .Select(d => d.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return Results.Ok(new LmStudioAvailableModelsResponse(ids, true));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                return Results.Ok(new LmStudioAvailableModelsResponse([], false));
            }
        });

        group.MapPut("/model", async (
            SetLmStudioModelRequest request, AppDbContext db, ILmStudioModelProvider modelProvider, CancellationToken ct) =>
        {
            var modelId = request.ModelId?.Trim();
            var row = await db.LmStudioModelConfigs.FirstOrDefaultAsync(ct);

            if (string.IsNullOrEmpty(modelId))
            {
                // Пусто/null — откат на фолбэк из appsettings/env, не отдельная "модель по
                // умолчанию" в БД (симметрично отсутствию активной PipelinePromptVersion).
                if (row is not null) db.LmStudioModelConfigs.Remove(row);
            }
            else if (row is null)
            {
                db.LmStudioModelConfigs.Add(new LmStudioModelConfig
                {
                    Id = Guid.NewGuid(), ModelId = modelId, UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                row.ModelId = modelId;
                row.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            modelProvider.Invalidate();
            return Results.NoContent();
        });
    }

    private record LmStudioModelResponse(string? ActiveModel, string FallbackModel);

    private record LmStudioAvailableModelsResponse(List<string> Models, bool LmStudioReachable);

    private record SetLmStudioModelRequest(string? ModelId);

    // --- DTO ответа LM Studio GET /v1/models (OpenAI-совместимый, только нужное поле) ---

    private sealed record ModelsListResponse([property: JsonPropertyName("data")] List<ModelDto>? Data);

    private sealed record ModelDto([property: JsonPropertyName("id")] string? Id);
}
