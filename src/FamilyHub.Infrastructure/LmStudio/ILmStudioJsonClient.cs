using System.Text.Json;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Generic-клиент локального OpenAI-совместимого сервера (LM Studio): отправляет промпт
/// (опционально с фото — vision-режим) и возвращает распарсенный JSON-ответ модели. Не знает
/// ничего про домен вызывающей стороны (медикаменты, суммаризация сниппетов и т.п.) — доменная
/// интерпретация полей остаётся там (например, FamilyHub.Modules.Medical.Ocr.MedicationOcrService,
/// FamilyHub.Modules.Medical.Enrichment.MedicationSummarizer). Переименован из ILmStudioVisionClient
/// (этап 4): "Vision" стало ложью, как только клиент пошёл по чисто текстовым запросам.
/// </summary>
public interface ILmStudioJsonClient
{
    Task<LmStudioJsonResult> ExtractJsonAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<(byte[] Bytes, string ContentType)> images,
        CancellationToken ct = default);

    /// <summary>Текстовый запрос без фото (суммаризация сниппетов и т.п.) — реализация делегирует в
    /// перегрузку с изображениями, передавая пустой список.</summary>
    Task<LmStudioJsonResult> ExtractJsonAsync(string systemPrompt, string userText, CancellationToken ct = default);
}

/// <summary>
/// Результат вызова: либо успешно распарсенный JSON-объект (значения как <see cref="JsonElement"/>,
/// т.к. типы полей заранее неизвестны), либо человекочитаемая ошибка (сеть, таймаут, невалидный JSON) —
/// без исключений наружу, чтобы вызывающий код мог показать пользователю понятный тост.
///
/// <see cref="IsTransient"/> — сбой ТЕХНИЧЕСКИЙ (сервер недоступен, таймаут, 5xx/429), а не
/// смысловой (невалидный JSON, пустой ответ, отказ по содержимому) — гейты выше по конвейеру
/// (LegitimacyGuardService/AnalytePlausibilityGuardService) пробрасывают его дальше, чтобы
/// процессоры могли отличить "модель отклонила" от "модель недоступна" и не хоронить задачу
/// навсегда, а дать Hangfire реально её повторить (см. план — заметки 5/6/7).
/// </summary>
public record LmStudioJsonResult(bool Success, Dictionary<string, JsonElement>? Payload, string? Error, bool IsTransient = false)
{
    public static LmStudioJsonResult Failure(string error, bool isTransient = false) => new(false, null, error, isTransient);
}
