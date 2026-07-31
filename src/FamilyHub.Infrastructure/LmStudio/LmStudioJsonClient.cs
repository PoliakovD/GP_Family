using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Типизированный HttpClient к LM Studio (эндпоинт /v1/chat/completions, OpenAI-совместимый).
/// Уровень "размышлений" (LmStudioOptions.ThinkingLevel, env LmStudio__ThinkingLevel=0..3)
/// применяется ТРЕМЯ независимыми способами (каждый следующий — подстраховка на случай, если
/// предыдущий не сработал для конкретной версии LM Studio/чат-шаблона):
/// 1) литерал "/no_think" (уровень None) или "/think" (Low/Medium/High) в конце пользовательского
///    сообщения — документированный Qwen3-переключатель на уровне самого чат-шаблона (Jinja
///    сканирует текст диалога), срабатывает даже когда параметры ниже бэкенд не поддерживает —
///    самый надёжный способ реально исключить размышление, а не просто скрыть его постфактум;
/// 2) request-level chat_template_kwargs.enable_thinking + thinking_budget и top-level
///    reasoning_effort — best-effort: градация Low/Medium/High реально различается, только если
///    загруженная модель/шаблон их понимает, иначе все три ведут себя как "думает сколько хочет";
/// 3) defensively — вырезание &lt;think&gt;...&lt;/think&gt; из ответа перед парсингом JSON, работает
///    гарантированно вне зависимости от того, сработали ли (1)/(2), но при включённом размышлении
///    НЕ ускоряет генерацию — блок всё равно был сгенерирован и просто отбрасывается при парсинге.
/// </summary>
public class LmStudioJsonClient(HttpClient httpClient, IOptions<LmStudioOptions> options, ILogger<LmStudioJsonClient> logger)
    : ILmStudioJsonClient
{
    private const string NoThinkDirective = "/no_think";
    private const string ThinkDirective = "/think";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex ThinkBlockRegex =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CodeFenceRegex =
        new(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc cref="ILmStudioJsonClient.ExtractJsonAsync(string, string, CancellationToken)"/>
    public Task<LmStudioJsonResult> ExtractJsonAsync(string systemPrompt, string userText, CancellationToken ct = default) =>
        ExtractJsonAsync(systemPrompt, userText, [], ct);

    public async Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default)
    {
        var thinkingLevel = options.Value.ThinkingLevel;
        var thinkingEnabled = thinkingLevel != LmStudioThinkingLevel.None;

        // "/no_think" / "/think" — переключатель самого чат-шаблона Qwen3 (см. докстринг класса),
        // а не API-параметр: работает даже когда chat_template_kwargs.enable_thinking проигнорирован.
        var directive = thinkingEnabled ? ThinkDirective : NoThinkDirective;
        var contentParts = new List<ContentPart> { new("text", Text: $"{userText}\n\n{directive}") };
        foreach (var (bytes, contentType) in images)
        {
            var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
            contentParts.Add(new ContentPart("image_url", ImageUrl: new ImageUrlPart(dataUrl)));
        }

        var request = new ChatCompletionRequest(
            Model: options.Value.Model,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", contentParts),
            ],
            Temperature: 0.1,
            Stream: false,
            ChatTemplateKwargs: new ChatTemplateKwargs(
                EnableThinking: thinkingEnabled,
                ThinkingBudget: thinkingEnabled ? ThinkingBudgetFor(thinkingLevel) : null),
            ReasoningEffort: thinkingEnabled ? ReasoningEffortFor(thinkingLevel) : null,
            MaxTokens: thinkingEnabled ? MaxTokensFor(thinkingLevel) : null);

        string? rawContent;
        try
        {
            using var response = await httpClient.PostAsJsonAsync("v1/chat/completions", request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var parsed = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct);
            rawContent = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "LM Studio недоступен или запрос по фото препарата превысил таймаут");
            return LmStudioJsonResult.Failure("Локальный сервер распознавания недоступен.");
        }

        if (string.IsNullOrWhiteSpace(rawContent))
        {
            logger.LogWarning("LM Studio вернул пустой ответ на запрос распознавания препарата");
            return LmStudioJsonResult.Failure("Модель вернула пустой ответ.");
        }

        try
        {
            var jsonPayload = ExtractJsonPayload(rawContent);
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonPayload, JsonOptions);
            if (payload is null)
            {
                logger.LogWarning("LM Studio: JSON распарсился в null. Сырой ответ: {Raw}", rawContent);
                return LmStudioJsonResult.Failure("Не удалось распознать структуру препарата на фото.");
            }

            return new LmStudioJsonResult(true, payload, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "LM Studio вернул невалидный JSON. Сырой ответ: {Raw}", rawContent);
            return LmStudioJsonResult.Failure("Не удалось распознать структуру препарата на фото.");
        }
    }

    /// <summary>Best-effort подсказка объёма рассуждения (chat_template_kwargs.thinking_budget,
    /// токены) — учитывается только если конкретная модель/шаблон её понимает, иначе игнорируется.</summary>
    private static int ThinkingBudgetFor(LmStudioThinkingLevel level) => level switch
    {
        LmStudioThinkingLevel.Low => 1024,
        LmStudioThinkingLevel.Medium => 4096,
        LmStudioThinkingLevel.High => 16384,
        _ => 1024,
    };

    /// <summary>Best-effort подсказка в духе OpenAI reasoning_effort — на случай, если бэкенд её
    /// поддерживает для загруженной модели; неизвестное поле большинство OpenAI-совместимых
    /// серверов просто игнорирует.</summary>
    private static string ReasoningEffortFor(LmStudioThinkingLevel level) => level switch
    {
        LmStudioThinkingLevel.Low => "low",
        LmStudioThinkingLevel.Medium => "medium",
        LmStudioThinkingLevel.High => "high",
        _ => "low",
    };

    /// <summary>С включённым размышлением ответ = reasoning-токены + сам JSON — без явного max_tokens
    /// сервер мог бы обрезать генерацию до того, как модель допишет JSON после долгого рассуждения.</summary>
    private static int MaxTokensFor(LmStudioThinkingLevel level) => level switch
    {
        LmStudioThinkingLevel.Low => 4096,
        LmStudioThinkingLevel.Medium => 8192,
        LmStudioThinkingLevel.High => 24576,
        _ => 4096,
    };

    /// <summary>Снимает &lt;think&gt;-блок и markdown-фенсы, подстраховкой вырезает от первой { до последней }.</summary>
    private static string ExtractJsonPayload(string content)
    {
        var noThink = ThinkBlockRegex.Replace(content, string.Empty).Trim();

        var fenced = CodeFenceRegex.Match(noThink);
        var candidate = fenced.Success ? fenced.Groups[1].Value : noThink;

        var start = candidate.IndexOf('{');
        var end = candidate.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            candidate = candidate.Substring(start, end - start + 1);
        }

        return candidate.Trim();
    }

    // --- OpenAI-совместимые DTO для запроса/ответа chat/completions ---

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("chat_template_kwargs")] ChatTemplateKwargs ChatTemplateKwargs,
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
        [property: JsonPropertyName("max_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxTokens = null);

    private sealed record ChatTemplateKwargs(
        [property: JsonPropertyName("enable_thinking")] bool EnableThinking,
        [property: JsonPropertyName("thinking_budget"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ThinkingBudget = null);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] object Content);

    private sealed record ContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text = null,
        [property: JsonPropertyName("image_url")] ImageUrlPart? ImageUrl = null);

    private sealed record ImageUrlPart([property: JsonPropertyName("url")] string Url);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}
