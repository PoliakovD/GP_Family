using System.Text.Json;
using System.Text.Json.Nodes;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Слияние гранулярных локов подполей payload (§4 плана «удобное редактирование справочника») —
/// расширяет прежнюю схему LockedFields ("displayName"/"payload"/"aliases", см. class doc
/// LabAnalyteKbWriter/KbWriter) элементами вида "payload.&lt;key&gt;": конкретный подключ jsonb,
/// залоченный редактором ФОРМЫ в админке (AdminCatalogService), когда правится не весь payload
/// разом, а одно поле формы (например, только refRanges, не всё описание показателя).
/// Автообогащение по-прежнему пишет payload целиком одним writer'ом — это не изменилось; данный
/// класс лишь переносит залоченные подключи из СТАРОГО payload в только что построенный НОВЫЙ
/// непосредственно перед записью, чтобы гранулярный лок не был неотличим от его отсутствия.
/// Лок всего "payload" (JSON-режим редактора) остаётся отдельной, более грубой веткой в
/// вызывающем коде — сюда не попадает вовсе (ANY(...) locked-check на "payload" делает SQL upsert).
/// </summary>
public static class KbPayloadLockMerger
{
    private const string PayloadKeyPrefix = "payload.";

    /// <summary>Ключи вида "payload.&lt;key&gt;" из LockedFields — пусто, если лок только на
    /// весь "payload" целиком (эту ветку обрабатывает ON CONFLICT DO UPDATE в SQL, не этот класс)
    /// или локов нет вовсе.</summary>
    public static IReadOnlyList<string> ExtractLockedPayloadKeys(IReadOnlyList<string> lockedFields) =>
        lockedFields
            .Where(f => f.StartsWith(PayloadKeyPrefix, StringComparison.Ordinal))
            .Select(f => f[PayloadKeyPrefix.Length..])
            .Where(k => k.Length > 0)
            .ToArray();

    /// <summary>Переносит значения залоченных ключей верхнего уровня payload из oldJson в newJson.
    /// Ключ, отсутствующий в oldJson (например, появился в схеме только сейчас), не переносится —
    /// новое значение остаётся, переносить нечего. Невалидный JSON с любой стороны — newJson без
    /// изменений (защитно: обе стороны уже проходят собственную JSON-валидацию раньше этой точки,
    /// сюда невалидный текст попасть не должен).</summary>
    public static string MergeLockedKeys(string oldJson, string newJson, IReadOnlyList<string> lockedKeys)
    {
        if (lockedKeys.Count == 0) return newJson;

        JsonNode? oldNode;
        JsonNode? newNode;
        try
        {
            oldNode = JsonNode.Parse(oldJson);
            newNode = JsonNode.Parse(newJson);
        }
        catch (JsonException)
        {
            return newJson;
        }

        if (oldNode is not JsonObject oldObj || newNode is not JsonObject newObj) return newJson;

        foreach (var key in lockedKeys)
        {
            if (oldObj.TryGetPropertyValue(key, out var oldValue))
                newObj[key] = oldValue?.DeepClone();
        }

        return newObj.ToJsonString();
    }
}
