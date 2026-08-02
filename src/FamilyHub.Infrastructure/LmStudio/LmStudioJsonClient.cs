using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Типизированный HttpClient к LM Studio (эндпоинт /v1/chat/completions, OpenAI-совместимый).
/// Уровень "размышлений" (LmStudioOptions.Reasoning, env LmStudio__Reasoning=none|minimal|medium|
/// maximum) НЕ управляется никаким API-параметром запроса — см. докстринг LmStudioReasoning:
/// ни chat_template_kwargs.enable_thinking, ни top-level reasoning_effort не дают предсказуемого
/// результата на разных моделях/квантизациях (проверено curl'ом). Канал рассуждений всегда
/// оставлен структурно включённым, а глубину задаёт словесная инструкция, дописанная в конец
/// системного промпта (см. ReasoningDirectives) — модель сама подстраивается под неё.
/// Вырезание &lt;think&gt;...&lt;/think&gt; из content — подстраховка на случай бэкенда, который
/// вкладывает рассуждение прямо в основной текст ответа, а не в отдельное поле reasoning_content.
/// </summary>
public class LmStudioJsonClient(HttpClient httpClient, IOptions<LmStudioOptions> options, ILogger<LmStudioJsonClient> logger)
    : ILmStudioJsonClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex ThinkBlockRegex =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CodeFenceRegex =
        new(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Текст, которым модели явно называется желаемая глубина размышлений — подобран и
    /// проверен curl'ом на bonsai-27b (см. LmStudioReasoning). Дописывается в конец системного
    /// промпта конкретной задачи (OCR/суммаризация), а не заменяет его.</summary>
    private static readonly Dictionary<LmStudioReasoning, string> ReasoningDirectives = new()
    {
        [LmStudioReasoning.None] = "Уровень рассуждений: без рассуждений. Не рассуждай перед ответом вообще — отвечай сразу и кратко, без промежуточных шагов.",
        [LmStudioReasoning.Minimal] = "Уровень рассуждений: минимальный. Рассуждай перед ответом по минимуму — только то, что действительно необходимо, коротко.",
        [LmStudioReasoning.Medium] = "Уровень рассуждений: средний. Рассуждай перед ответом в разумных пределах — по существу, без лишних деталей.",
        [LmStudioReasoning.Maximum] = "Уровень рассуждений: максимальный. Рассуждай перед ответом подробно и тщательно, проверяя себя на каждом шаге, прежде чем дать финальный ответ.",
    };

    /// <inheritdoc cref="ILmStudioJsonClient.ExtractJsonAsync(string, string, CancellationToken)"/>
    public Task<LmStudioJsonResult> ExtractJsonAsync(string systemPrompt, string userText, CancellationToken ct = default) =>
        ExtractJsonAsync(systemPrompt, userText, [], ct);

    public async Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default)
    {
        var systemPromptWithReasoning = $"{systemPrompt}\n\n{ReasoningDirectives[options.Value.Reasoning]}";

        var contentParts = new List<ContentPart> { new("text", Text: userText) };
        foreach (var (bytes, contentType) in images)
        {
            var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
            contentParts.Add(new ContentPart("image_url", ImageUrl: new ImageUrlPart(dataUrl)));
        }

        var request = new ChatCompletionRequest(
            Model: options.Value.Model,
            Messages:
            [
                new ChatMessage("system", systemPromptWithReasoning),
                new ChatMessage("user", contentParts),
            ],
            Temperature: 0.1,
            Stream: false);

        string? rawContent;
        try
        {
            using var response = await httpClient.PostAsJsonAsync("v1/chat/completions", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Тело ответа LM Studio на 4xx обычно объясняет, что именно не понравилось в
                // запросе — логируем его, а не только код: EnsureSuccessStatusCode() выбросил бы
                // HttpRequestException без тела, и причина терялась бы за общим "недоступен".
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "LM Studio вернул {StatusCode} на запрос: {Body}", (int)response.StatusCode, errorBody);
                return LmStudioJsonResult.Failure($"Локальный сервер распознавания вернул ошибку {(int)response.StatusCode}.");
            }

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
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] object Content);

    private sealed record ContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text = null,
        // WhenWritingNull: для текстовых частей (без фото) не шлём лишний "image_url": null —
        // возможный источник 400 у строгих OpenAI-совместимых валидаторов схемы запроса.
        [property: JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImageUrlPart? ImageUrl = null);

    private sealed record ImageUrlPart([property: JsonPropertyName("url")] string Url);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content);
}
