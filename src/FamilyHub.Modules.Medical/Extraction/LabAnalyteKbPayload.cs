using System.Text.Json;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Сериализация/чтение GlobalLabAnalyteKb.PayloadJson — единственное место, которое знает форму
/// этого jsonb-поля (пишет LabAnalyteKbWriter, читает MedicalDocumentExtractionProcessor при
/// подстановке референсного диапазона из справочника).
/// </summary>
public static class LabAnalyteKbPayload
{
    public static string Build(LabAnalyteSummary summary) => JsonSerializer.Serialize(new
    {
        schemaVersion = LabAnalyteSummarySchema.CurrentVersion,
        loincCode = summary.LoincCode,
        defaultUnit = summary.DefaultUnit,
        plainExplanation = summary.PlainExplanation,
        whyMeasured = summary.WhyMeasured,
        highMeans = summary.HighMeans,
        lowMeans = summary.LowMeans,
        refRanges = summary.RefRanges.Select(r => new
        {
            ageFrom = r.AgeFrom, ageTo = r.AgeTo, low = r.Low, high = r.High, unit = r.Unit,
        }),
    });

    /// <summary>Невалидный/чужой JSON — не считается ошибкой конвейера, просто пустой список
    /// (справочник даёт лишь ориентир поверх референса из самого бланка).</summary>
    public static List<KbReferenceRange> ParseRefRanges(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("refRanges", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<KbReferenceRange>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                result.Add(new KbReferenceRange(
                    AgeFrom: ReadInt(el, "ageFrom"),
                    AgeTo: ReadInt(el, "ageTo"),
                    Low: ReadDouble(el, "low"),
                    High: ReadDouble(el, "high"),
                    Unit: el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int? ReadInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? ReadDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
