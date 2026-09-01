using System.Text.Json;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Сериализация/чтение GlobalLabAnalyteKb.PayloadJson — единственное место, которое знает форму
/// этого jsonb-поля (пишет LabAnalyteKbWriter, читает MedicalDocumentExtractionProcessor при
/// подстановке референсного диапазона из справочника / PatientReferenceCalculator при расчёте).
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
        calculationInstructions = summary.CalculationInstructions,
        relatedNames = summary.RelatedAnalytes ?? [],
        refRanges = summary.RefRanges.Select(r => new
        {
            ageFrom = r.AgeFrom, ageTo = r.AgeTo, sex = SexToString(r.Sex), low = r.Low, high = r.High, unit = r.Unit,
            normKind = NormKindToString(r.NormKind),
            population = PopulationToString(r.Population),
            populationDetail = r.PopulationDetail,
            sourceDomain = r.SourceDomain,
            sourceRank = r.SourceRank,
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
                    Sex: ParseSex(el.TryGetProperty("sex", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null),
                    Low: ReadDouble(el, "low"),
                    High: ReadDouble(el, "high"),
                    Unit: el.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                    // v1-v3 не писали эти поля — читаются как FixedRange/General (см. class doc
                    // LabAnalyteSummarySchema), а не как ошибка парсинга.
                    NormKind: ParseNormKind(ReadStringProp(el, "normKind")),
                    Population: ParsePopulation(ReadStringProp(el, "population")),
                    PopulationDetail: ReadStringProp(el, "populationDetail"),
                    SourceDomain: ReadStringProp(el, "sourceDomain"),
                    SourceRank: ReadInt(el, "sourceRank") ?? 0));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Null, если справочник не даёт числового диапазона — PatientReferenceCalculator
    /// пробует расчёт по методике, ParseRefRanges на это не отвечает (см. Build выше).</summary>
    public static string? ParseCalculationInstructions(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("calculationInstructions", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Пустой список у строк v1/v2 (поле появилось только в v3) — не ошибка, просто
    /// "Что смотрят вместе" пока не заполнено для этого показателя (см. схема выше).</summary>
    public static List<string> ParseRelatedNames(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("relatedNames", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            return arr.EnumerateArray()
                .Where(el => el.ValueKind == JsonValueKind.String)
                .Select(el => el.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? SexToString(Gender? sex) => sex switch
    {
        Gender.Male => "male",
        Gender.Female => "female",
        _ => null,
    };

    private static Gender? ParseSex(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "male" => Gender.Male,
        "female" => Gender.Female,
        _ => null,
    };

    private static string NormKindToString(LabNormKind kind) => kind switch
    {
        LabNormKind.Calculated => "calculated",
        LabNormKind.Qualitative => "qualitative",
        _ => "fixed",
    };

    private static LabNormKind ParseNormKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "calculated" => LabNormKind.Calculated,
        "qualitative" => LabNormKind.Qualitative,
        _ => LabNormKind.FixedRange,
    };

    private static string PopulationToString(LabPopulation population) => population switch
    {
        LabPopulation.Pregnancy => "pregnancy",
        LabPopulation.Children => "children",
        LabPopulation.CyclePhase => "cyclePhase",
        _ => "general",
    };

    private static LabPopulation ParsePopulation(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pregnancy" => LabPopulation.Pregnancy,
        "children" => LabPopulation.Children,
        "cyclephase" or "cycle_phase" or "cycle-phase" => LabPopulation.CyclePhase,
        _ => LabPopulation.General,
    };

    private static string? ReadStringProp(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? ReadDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}
