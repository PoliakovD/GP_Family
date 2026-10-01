using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Хэш канонического payload записи справочника (ADR-0018): сравнение «изменился ли payload с момента
/// проверки» без зависимости от пробелов и порядка ключей (jsonb в Postgres переупорядочивает ключи и
/// нормализует пробелы, а писатель сериализует по-своему). Канонизация: ключи объектов по алфавиту
/// (рекурсивно), порядок элементов массивов сохраняется.
/// </summary>
public static class KbPayloadHash
{
    public static string Compute(string payloadJson)
    {
        string canonical;
        try
        {
            var node = JsonNode.Parse(payloadJson);
            canonical = Canonicalize(node);
        }
        catch (JsonException)
        {
            // Невалидный JSON в payload не должен ронять запись — хэшируем как есть (сравнение останется детерминированным).
            canonical = payloadJson;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject obj:
                return "{" + string.Join(",", obj
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonicalize(p.Value))) + "}";
            case JsonArray arr:
                return "[" + string.Join(",", arr.Select(Canonicalize)) + "]";
            default:
                return node.ToJsonString();
        }
    }
}
