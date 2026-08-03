using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Ocr;

/// <summary>
/// Оцифровка медикамента по фото упаковки/этикетки через локальную vision-LLM (LM Studio).
/// Фото не сохраняются — используются только для распознавания в рамках одного запроса.
/// </summary>
public class MedicationOcrService(ILmStudioJsonClient client, ILogger<MedicationOcrService> logger)
{
    private const int MaxPhotos = 5;

    /// <summary>Клиент сжимает фото до FullHD перед отправкой — этого с большим запасом хватает
    /// (см. аудит module-review-2026-08-02/04, находка 2). Явный лимит вместо неявного дефолта
    /// Kestrel — не только для честного клиента: без него много крупных фото → большие
    /// base64-payload'ы в памяти процесса и на инференс (локальный resource-exhaustion).</summary>
    private const long MaxPhotoSizeBytes = 1 * 1024 * 1024;

    private const string UserText = "Определи препарат по этим фотографиям (их может быть от 1 до 5, все — один и тот же препарат).";

    private const string SystemPrompt = """
        Ты — оцифровщик для медицинских препаратов по фотографиям упаковки/этикетки/ампулы.
        Проанализируй все прикреплённые фотографии одного и того же препарата и верни ТОЛЬКО
        валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "name": "Название препарата",
          "expiryDate": "Дата истечения срока годности в формате dd/MM/yyyy",
          "fields": [
            { "name": "Производитель", "value": "..." },
            { "name": "Дата производства", "value": "дата в формате dd/MM/yyyy" },
            { "name": "Тип", "value": "капли/таблетки/ампулы/сироп/мазь и т.д." },
            { "name": "Дозировка", "value": "..." },
            { "name": "Действующее вещество", "value": "..." }
          ]
        }

        Правила:
        - "name" — используй null, если название не удалось определить.
        - Каждый элемент "fields" — объект с двумя ключами: "name" (человекочитаемое название
          параметра НА РУССКОМ ЯЗЫКЕ) и "value" (само значение).
        - Если какое-то поле из примера выше не удалось определить на фото — просто не включай
          его в "fields" (не добавляй с пустым или null значением).
        - Если на фото дата в формате MM/yyyy (только месяц и год) — подставляй 1 число: 01/MM/yyyy.
        - Если нашёл на фото доп. информацию, не покрытую примером выше (серия/партия, штрихкод,
          объём, тип упаковки — банка/коробка/блистер, способ хранения и т.д.) — добавь её
          дополнительными элементами в "fields", тоже с "name" на русском.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<MedicationOcrResponse> ExtractAsync(IFormFileCollection files, CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return Failure("Прикрепите хотя бы одно фото.");
        }

        if (files.Count > MaxPhotos)
        {
            return Failure($"Можно прикрепить не более {MaxPhotos} фото.");
        }

        var images = new List<(byte[] Bytes, string ContentType)>();
        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.ContentType) ||
                !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("Допустимы только изображения.");
            }

            // Проверка ДО буферизации в память — большой файл не должен даже попадать в MemoryStream.
            if (file.Length > MaxPhotoSizeBytes)
            {
                return Failure($"Каждое фото должно быть не больше {MaxPhotoSizeBytes / (1024 * 1024)} МБ.");
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            images.Add((ms.ToArray(), file.ContentType));
        }

        var result = await client.ExtractJsonAsync(SystemPrompt, UserText, images, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Распознавание препарата по фото не удалось: {Error}", result.Error);
            return Failure(result.Error ?? "Не удалось распознать препарат по фото.");
        }

        var name = TryGetValue(result.Payload, "name", out var nameEl) ? StringifyJsonElement(nameEl) : null;
        var expiryDate = TryGetValue(result.Payload, "expiryDate", out var expEl) ? StringifyJsonElement(expEl) : null;

        var data = new Dictionary<string, string>();
        if (TryGetValue(result.Payload, "fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fieldEl in fieldsEl.EnumerateArray())
            {
                if (fieldEl.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetProperty(fieldEl, "name", out var fieldNameEl)) continue;
                if (!TryGetProperty(fieldEl, "value", out var fieldValueEl)) continue;

                var fieldName = StringifyJsonElement(fieldNameEl).Trim();
                var fieldValue = StringifyJsonElement(fieldValueEl).Trim();
                if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(fieldValue)) continue;

                data[fieldName] = fieldValue;
            }
        }

        return new MedicationOcrResponse(
            true,
            string.IsNullOrWhiteSpace(name) ? null : name,
            string.IsNullOrWhiteSpace(expiryDate) ? null : expiryDate,
            data,
            null);
    }

    private static MedicationOcrResponse Failure(string error) => new(false, null, null, null, error);

    private static bool TryGetValue(Dictionary<string, JsonElement> payload, string key, out JsonElement value)
    {
        foreach (var (k, v) in payload)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Регистронезависимый поиск свойства внутри объекта (модель иногда меняет регистр ключей).</summary>
    private static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string StringifyJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };
}
