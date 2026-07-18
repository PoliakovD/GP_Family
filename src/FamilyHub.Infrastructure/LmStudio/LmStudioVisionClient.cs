using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Типизированный HttpClient к LM Studio (эндпоинт /v1/chat/completions, OpenAI-совместимый).
/// Отключает "thinking" режим модели двумя способами: request-level флагом
/// chat_template_kwargs.enable_thinking (best-effort — поддержка зависит от версии LM Studio
/// и chat-шаблона модели) и defensively — вырезанием &lt;think&gt;...&lt;/think&gt; из ответа перед
/// парсингом JSON, что работает гарантированно вне зависимости от того, сработал ли флаг.
/// </summary>
public class LmStudioVisionClient(HttpClient httpClient, IOptions<LmStudioOptions> options, ILogger<LmStudioVisionClient> logger)
    : ILmStudioVisionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex ThinkBlockRegex =
        new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CodeFenceRegex =
        new(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default)
    {
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
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", contentParts),
            ],
            Temperature: 0.1,
            Stream: false,
            ChatTemplateKwargs: new ChatTemplateKwargs(EnableThinking: false));

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
        [property: JsonPropertyName("chat_template_kwargs")] ChatTemplateKwargs ChatTemplateKwargs);

    private sealed record ChatTemplateKwargs(
        [property: JsonPropertyName("enable_thinking")] bool EnableThinking);

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
