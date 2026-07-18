using System.Text.Json;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Generic-клиент локального OpenAI-совместимого сервера (LM Studio) с vision-LLM: отправляет
/// фото + промпт, возвращает распарсенный JSON-ответ модели. Не знает ничего про медикаменты —
/// доменная интерпретация полей (какие ключи что значат) остаётся на вызывающей стороне
/// (например, FamilyHub.Modules.Medical.Ocr.MedicationOcrService).
/// </summary>
public interface ILmStudioVisionClient
{
    Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default);
}

/// <summary>
/// Результат вызова: либо успешно распарсенный JSON-объект (значения как <see cref="JsonElement"/>,
/// т.к. типы полей заранее неизвестны), либо человекочитаемая ошибка (сеть, таймаут, невалидный JSON) —
/// без исключений наружу, чтобы вызывающий код мог показать пользователю понятный тост.
/// </summary>
public record LmStudioJsonResult(bool Success, Dictionary<string, JsonElement>? Payload, string? Error)
{
    public static LmStudioJsonResult Failure(string error) => new(false, null, error);
}
