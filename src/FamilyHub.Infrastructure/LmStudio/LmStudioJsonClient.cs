using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FamilyHub.Domain.Enums;
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
/// Поле "model" запроса берётся из ILmStudioModelProvider (админка), не напрямую из
/// LmStudioOptions.Model — переключение модели из UI не требует передеплоя.
/// </summary>
public class LmStudioJsonClient(
    HttpClient httpClient, IOptions<LmStudioOptions> options, LmStudioConcurrencyGate gate,
    ILmStudioModelProvider modelProvider, LlmThinkingReportService thinkingReporter, ILogger<LmStudioJsonClient> logger)
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

    /// <summary>Второй, узко-специализированный проход — вызывается только когда первичный ответ
    /// не распарсился как JSON (см. ExtractJsonAsync). Несмотря на промпт задачи, модель иногда
    /// отдаёт почти-валидный JSON с мелким синтаксическим дефектом (пропущенная запятая,
    /// незакрытая кавычка/скобка) — просить её поправить СИНТАКСИС, не переосмысливая задачу
    /// заново, восстанавливает заметную долю таких случаев вместо того, чтобы сразу проваливать
    /// вызов. Задача узкая и механическая — директива уровня рассуждений (см. ReasoningDirectives)
    /// сюда не примешивается.</summary>
    private const string JsonRepairSystemPrompt = """
        Ты — специалист по исправлению синтаксиса JSON. На входе — текст, который должен быть
        валидным JSON-объектом, но не парсится (пропущенная запятая, незакрытая кавычка/скобка,
        лишняя запятая перед закрывающей скобкой, неэкранированный спецсимвол внутри строки и
        т.п.). Верни ТОЛЬКО исправленный JSON, без пояснений, без markdown, без блока <think>.

        Правила:
        - Исправляй ТОЛЬКО синтаксис — ни одно значение или ключ не должны измениться по смыслу.
        - Если это в принципе не похоже на JSON или понять, что именно сломано, невозможно —
          верни входной текст как есть, ничего не придумывая вместо него.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    /// <inheritdoc cref="ILmStudioJsonClient.ExtractJsonAsync(string, string, CancellationToken, bool)"/>
    public Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt, string userText, CancellationToken ct = default, bool suppressThinking = false) =>
        ExtractJsonAsync(systemPrompt, userText, [], ct, suppressThinking);

    public async Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default,
        bool suppressThinking = false)
    {
        var reasoning = await modelProvider.GetActiveReasoningAsync(options.Value.Reasoning, ct);
        var systemPromptWithReasoning = $"{systemPrompt}\n\n{ReasoningDirectives[reasoning]}";

        var (rawContent, sendError, isTransient) =
            await SendChatCompletionAsync(systemPromptWithReasoning, userText, images, suppressThinking, ct);
        if (sendError is not null) return LmStudioJsonResult.Failure(sendError, isTransient);
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            logger.LogWarning("LM Studio вернул пустой ответ на запрос распознавания препарата");
            return LmStudioJsonResult.Failure("Модель вернула пустой ответ.");
        }

        var (success, payload, candidate, parseError) = TryParseJson(rawContent);
        if (success) return new LmStudioJsonResult(true, payload, null);

        // Фолбэк на невалидный JSON — несмотря на промпт задачи, модель иногда отдаёт
        // почти-валидный JSON с мелким синтаксическим дефектом (см. class doc JsonRepairSystemPrompt).
        // Один дополнительный узкий проход "почини синтаксис" восстанавливает заметную долю таких
        // случаев вместо того, чтобы сразу проваливать вызов целиком.
        logger.LogInformation(
            "LM Studio вернул невалидный JSON ({Error}) — пробуем починить отдельным проходом. Сырой ответ: {Raw}",
            parseError, rawContent);

        var (repairedRaw, repairSendError, repairIsTransient) =
            await SendChatCompletionAsync(JsonRepairSystemPrompt, candidate, [], suppressThinking, ct);
        if (repairSendError is not null || string.IsNullOrWhiteSpace(repairedRaw))
        {
            logger.LogWarning("LM Studio: починка JSON недоступна ({Error}).", repairSendError ?? "пустой ответ");
            return repairSendError is not null
                ? LmStudioJsonResult.Failure("Модель вернула невалидный JSON, и починить его не удалось.", repairIsTransient)
                : LmStudioJsonResult.Failure("Модель вернула невалидный JSON, и починить его не удалось.");
        }

        var (repairSuccess, repairedPayload, _, repairParseError) = TryParseJson(repairedRaw);
        if (repairSuccess)
        {
            logger.LogInformation("LM Studio: JSON успешно починен отдельным проходом.");
            return new LmStudioJsonResult(true, repairedPayload, null);
        }

        logger.LogWarning(
            "LM Studio: JSON всё ещё невалиден после попытки починки ({Error}). Исходный ответ: {Raw}, после починки: {Repaired}",
            repairParseError, rawContent, repairedRaw);
        return LmStudioJsonResult.Failure("Модель вернула невалидный JSON, и починить его не удалось.");
    }

    /// <summary>Единая точка сериализации всех вызовов LM Studio (аудит, находка High #2) —
    /// физически единственный инстанс модели за WireGuard не выдержит параллельных запросов;
    /// раньше это соблюдалось только фоновым конвейером (WorkerCount=1 на Hangfire-очередях), но
    /// не синхронным OCR-эндпоинтом, который шёл сюда напрямую из HTTP-запроса. Используется и
    /// основным вызовом, и починкой JSON (ExtractJsonAsync) — оба варианта одного и того же
    /// физического запроса к модели.
    ///
    /// Живой поток "мыслей" (план) — Stream: true только когда ambient job-контекст установлен
    /// (см. LmStudioThinkingContext) И вызывающий не передал suppressThinking (гейты). Вне этого
    /// условия — Stream: false, путь ниже НЕ ТРОГАЕТСЯ и ведёт себя ровно как до этой задачи;
    /// риск регрессии сосредоточен целиком в новой ветке (SendStreamingChatCompletionAsync), не в
    /// уже существующем поведении.</summary>
    private async Task<(string? RawContent, string? Error, bool IsTransient)> SendChatCompletionAsync(
        string systemPrompt, string userText, IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        bool suppressThinking, CancellationToken ct)
    {
        var contentParts = new List<ContentPart> { new("text", Text: userText) };
        foreach (var (bytes, contentType) in images)
        {
            var dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
            contentParts.Add(new ContentPart("image_url", ImageUrl: new ImageUrlPart(dataUrl)));
        }

        var thinkingScope = suppressThinking ? null : LmStudioThinkingContext.Current;

        var model = await modelProvider.GetActiveModelAsync(options.Value.Model, ct);
        var request = new ChatCompletionRequest(
            Model: model,
            Messages:
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", contentParts),
            ],
            Temperature: 0.1,
            Stream: thinkingScope is not null);

        await gate.WaitAsync(ct);
        try
        {
            if (thinkingScope is null)
            {
                using var response = await httpClient.PostAsJsonAsync("v1/chat/completions", request, JsonOptions, ct);
                if (!response.IsSuccessStatusCode)
                    return (null, await BuildErrorAsync(response, ct), IsTransientStatus(response.StatusCode));

                var parsed = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, ct);
                return (parsed?.Choices?.FirstOrDefault()?.Message?.Content, null, false);
            }

            // ResponseHeadersRead — тело читаем построчно ниже (SendStreamingChatCompletionAsync),
            // не ждём, пока весь ответ придёт целиком (это и есть смысл потока).
            using var streamingRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            using var streamingResponse = await httpClient.SendAsync(streamingRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!streamingResponse.IsSuccessStatusCode)
                return (null, await BuildErrorAsync(streamingResponse, ct), IsTransientStatus(streamingResponse.StatusCode));

            var rawContent = await SendStreamingChatCompletionAsync(streamingResponse, thinkingScope, ct);
            return (rawContent, null, false);
        }
        // !ct.IsCancellationRequested исключает из этого catch отмену САМИМ вызывающим (аудит,
        // находка Medium #1) — TaskCanceledException прилетает и от клиентского HttpClient.Timeout
        // (внутренний, не наш ct), и от отмены переданным ct (остановка хоста, обрыв запроса).
        // Раньше оба случая превращались в одинаковый "Локальный сервер недоступен" — бизнес-исход,
        // который Hangfire НЕ ретраит (RunAsync завершается штатно, без исключения). Из-за этого
        // редеплой API посреди распознавания молча терял попытку вместо того, чтобы дать Hangfire
        // повторить задачу: отмена нашим ct теперь просто пробрасывается дальше как есть.
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "LM Studio недоступен или запрос по фото препарата превысил таймаут");
            if (thinkingScope is not null) await thinkingReporter.ClearAsync(thinkingScope.Kind, thinkingScope.JobId, CancellationToken.None);
            return (null, "Локальный сервер распознавания недоступен.", true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Тело ответа LM Studio на 4xx обычно объясняет, что именно не понравилось в
    /// запросе — логируем его, а не только код: EnsureSuccessStatusCode() выбросил бы
    /// HttpRequestException без тела, и причина терялась бы за общим "недоступен".</summary>
    private async Task<string> BuildErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var errorBody = await response.Content.ReadAsStringAsync(ct);
        var statusCode = (int)response.StatusCode;
        logger.LogWarning("LM Studio вернул {StatusCode} на запрос: {Body}", statusCode, errorBody);
        return $"Локальный сервер распознавания вернул ошибку {statusCode}.";
    }

    /// <summary>5xx/429 — сервер жив, но временно не может ответить (перегружен, модель ещё
    /// грузится и т.п.); 4xx (кроме 429) — постоянная проблема самого запроса, повтор без
    /// изменений ничего не даст.</summary>
    private static bool IsTransientStatus(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode >= 500 || statusCode == System.Net.HttpStatusCode.TooManyRequests;

    /// <summary>Читает SSE-поток chat/completions (`data: {...}` построчно, `data: [DONE]` —
    /// конец) и накапливает контент дельта-за-дельтой через ThinkTagAccumulator — на каждый
    /// непустой прирост "мысли" внутри ещё не закрытого &lt;think&gt; шлёт throttled-репорт (см.
    /// LlmThinkingReportService.ReportAsync). Возвращает полный накопленный текст — то же самое,
    /// что вернул бы Stream: false одним куском, дальше по коду (TryParseJson/ExtractJsonPayload)
    /// обрабатывается совершенно одинаково для обоих путей.</summary>
    private async Task<string?> SendStreamingChatCompletionAsync(
        HttpResponseMessage response, LmStudioThinkingContext.Scope scope, CancellationToken ct)
    {
        var accumulator = new ThinkTagAccumulator();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]") break;

                string? delta;
                try
                {
                    var chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(payload, JsonOptions);
                    delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
                }
                catch (JsonException)
                {
                    // Единичная неразборчивая строка чанка — пропускаем её, не весь ответ; LM
                    // Studio иногда шлёт keep-alive/служебные события того же вида "data: ...".
                    continue;
                }

                var thinking = accumulator.AppendDelta(delta);
                if (thinking is not null)
                    await thinkingReporter.ReportAsync(scope.Kind, scope.JobId, thinking, ct);
            }
        }
        finally
        {
            // Мысль либо уже закрылась (accumulator сам вернул null на последних дельтах), либо
            // вызов прервался — в обоих случаях после этого метода "текущая мысль" не должна
            // висеть на экране устаревшей: следующий шаг конвейера/показ "Готово" её не перезатрёт
            // сам.
            await thinkingReporter.ClearAsync(scope.Kind, scope.JobId, CancellationToken.None);
        }

        return accumulator.FullContent;
    }

    /// <summary>Извлекает JSON-подстроку (см. ExtractJsonPayload) и пытается её распарсить —
    /// Candidate возвращается всегда (даже при неудаче), это и есть вход для починки JSON
    /// отдельным проходом (ExtractJsonAsync) — уже избавленный от &lt;think&gt;/markdown, узкая
    /// цель для повторного запроса, а не сырой текст с посторонним контекстом.</summary>
    private static (bool Success, Dictionary<string, JsonElement>? Payload, string Candidate, string? Error) TryParseJson(string rawContent)
    {
        var candidate = ExtractJsonPayload(rawContent);
        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(candidate, JsonOptions);
            return payload is null
                ? (false, null, candidate, "JSON распарсился в null")
                : (true, payload, candidate, null);
        }
        catch (JsonException ex)
        {
            return (false, null, candidate, ex.Message);
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

    // --- DTO одного SSE-чанка стримингового ответа (Stream: true) — план "живой поток мыслей".
    // Тот же choices[].delta.content, что и у любого OpenAI-совместимого стрима; LM Studio не
    // отдаёт reasoning_content отдельным полем для этой модели/пресета (проверено), поэтому
    // &lt;think&gt; приходит прямо внутри content, как и в нестриминговом ответе. ---

    private sealed record ChatCompletionChunk(
        [property: JsonPropertyName("choices")] List<ChatChunkChoice>? Choices);

    private sealed record ChatChunkChoice(
        [property: JsonPropertyName("delta")] ChatChunkDelta? Delta);

    private sealed record ChatChunkDelta(
        [property: JsonPropertyName("content")] string? Content);
}
