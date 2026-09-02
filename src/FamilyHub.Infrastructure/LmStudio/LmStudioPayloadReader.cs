using System.Text.Json;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Регистронезависимое чтение полей из <see cref="LmStudioJsonResult.Payload"/> — модель иногда
/// меняет регистр ключей JSON-ответа. Было продублировано в MedicationOcrService и
/// MedicationSummarizer до появления третьего/четвёртого потребителя (ветка medicalrecords:
/// LmStudioMedicalDocumentExtractor, LabSummarizer) — вынесено сюда по тому же принципу, что и
/// PasswordRules/UsernameRules в Domain: как только правило нужно двум+ независимым
/// потребителям, дублирование дальше только расходится.
/// </summary>
public static class LmStudioPayloadReader
{
    public static bool TryGetValue(Dictionary<string, JsonElement> payload, string key, out JsonElement value)
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

    /// <summary>Регистронезависимый поиск свойства внутри объекта (не корневого payload) — та же
    /// причина, что у TryGetValue.</summary>
    public static bool TryGetProperty(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static string? ReadString(Dictionary<string, JsonElement> payload, string key) =>
        TryGetValue(payload, key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public static string? ReadString(JsonElement obj, string key) =>
        TryGetProperty(obj, key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public static double? ReadDouble(JsonElement obj, string key)
    {
        if (!TryGetProperty(obj, key, out var el)) return null;
        return ParseDouble(el);
    }

    public static double? ReadDouble(Dictionary<string, JsonElement> payload, string key) =>
        TryGetValue(payload, key, out var el) ? ParseDouble(el) : null;

    private static double? ParseDouble(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetDouble(out var d) => d,
        JsonValueKind.String when double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static List<string> ReadStringArray(Dictionary<string, JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el) || el.ValueKind != JsonValueKind.Array) return [];
        return ReadStringArray(el);
    }

    public static List<string> ReadStringArray(JsonElement arrayElement)
    {
        if (arrayElement.ValueKind != JsonValueKind.Array) return [];
        return arrayElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static List<int> ReadIndexArray(Dictionary<string, JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el) || el.ValueKind != JsonValueKind.Array) return [];
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
            .Select(e => e.GetInt32())
            .ToList();
    }

    /// <summary>Печатаемое представление любого JSON-значения — строка как есть, числа/булевы
    /// через ToString, объекты/массивы через сырой JSON (см. MedicationOcrService.StringifyJsonElement,
    /// тот же приём).</summary>
    public static string Stringify(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };
}
