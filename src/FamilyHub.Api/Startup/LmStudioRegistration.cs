using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — локальная LLM (текст + vision): гейт
/// сериализации, живой поток "мыслей", позиция в очереди, typed HttpClient, провайдеры
/// промптов/модели, без изменения поведения/порядка.
/// </summary>
public static class LmStudioRegistration
{
    public static WebApplicationBuilder AddFamilyHubLmStudio(this WebApplicationBuilder builder)
    {
        // --- LM Studio: локальная LLM (текст + vision) — оцифровка медикаментов по фото (не хранит
        // --- фото) и суммаризация веб-сниппетов для справочника (этап 4) ---
        // Singleton-гейт (аудит, находка High #2): единственная точка сериализации всех вызовов LM
        // Studio (LmStudioJsonClient) — физически один ноутбук за WireGuard, второй одновременный запрос
        // прежде мог прийти в обход дисциплины WorkerCount=1 фоновых Hangfire-очередей через синхронный
        // OCR-эндпоинт (POST /api/medications/ocr).
        builder.Services.AddSingleton<LmStudioConcurrencyGate>();
        // Живой поток "мыслей" модели (план) — throttled-запись CurrentThought на нужную из четырёх
        // таблиц задач, см. class doc. Scoped (берёт AppDbContext) — безопасно как зависимость типизированного
        // HttpClient ниже (тот резолвится внутри того же DI-скоупа, что и вызывающий Hangfire-job/HTTP-запрос).
        builder.Services.AddScoped<LlmThinkingReportService>();
        // Реальная позиция в ОБЩЕЙ очереди к единственному локальному LLM (не только своей таблицы задач)
        // — "extraction" и "enrichment" — разные Hangfire-серверы, задача может дойти до Running в обеих
        // одновременно, и только LmStudioConcurrencyGate решает, кто говорит с моделью прямо сейчас.
        builder.Services.AddScoped<LlmQueuePositionService>();
        builder.Services.AddHttpClient<ILmStudioJsonClient, LmStudioJsonClient>((sp, client) =>
        {
            var lmStudioOptions = sp.GetRequiredService<IOptions<LmStudioOptions>>().Value;
            client.BaseAddress = new Uri(lmStudioOptions.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(lmStudioOptions.TimeoutSeconds);
        });

        // Управление enrich-пайплайном из админки (§2) — резолвинг активного текста промпта/шаблона по
        // ключу, версионируется в БД (PipelinePromptVersion). Infrastructure-уровня (не Modules.Medical):
        // используется и LLM-промптами Modules.Medical.Extraction/Enrichment, и шаблонами поисковых
        // запросов во внешний поиск здесь же в Infrastructure.Enrichment (AnalyteSearchQueryBuilder,
        // BraveSearchProvider/YandexSearchProvider) — см. class doc IPromptProvider.
        builder.Services.AddScoped<PromptProvider>();
        builder.Services.AddScoped<IPromptProvider>(sp => sp.GetRequiredService<PromptProvider>());
        builder.Services.AddScoped<FamilyHub.Infrastructure.Enrichment.AnalyteSearchQueryBuilder>();

        // Выбор активной модели LM Studio из админки — тот же приём, что PromptProvider выше.
        builder.Services.AddScoped<LmStudioModelProvider>();
        builder.Services.AddScoped<ILmStudioModelProvider>(sp => sp.GetRequiredService<LmStudioModelProvider>());

        // Доступность LM Studio (GET /v1/models) — общая реализация для /health/llm и
        // LmStudioRecoverySweepJob (см. класс-doc ILmStudioAvailabilityProbe).
        builder.Services.AddSingleton<ILmStudioAvailabilityProbe, LmStudioAvailabilityProbe>();

        return builder;
    }
}
