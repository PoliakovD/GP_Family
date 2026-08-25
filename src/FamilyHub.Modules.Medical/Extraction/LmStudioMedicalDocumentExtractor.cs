using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Реализация конвейера извлечения через локальный LM Studio (ветка medicalrecords, задачи
/// 5.2/5.3). Диспетчеризация формата — <see cref="IDocumentTextExtractor"/> (Infrastructure):
/// текстовый путь дёшев и точен, vision-OCR — только для фото и PDF-сканов (см. план). Длинный
/// документ режется на куски (текст) или обрабатывается постранично (картинки) — один вызов
/// модели на кусок/страницу, результаты сливаются с дедупом.
///
/// Антигаллюцинационный гейт для анализов (прецедент — MedicationSummarizer): на текстовом пути
/// имя каждого извлечённого показателя обязано нормализованно встречаться в исходном тексте
/// куска — показатель, которого модель "не увидела" в тексте, а придумала, отбрасывается. На
/// vision-пути такой проверки нет по построению (исходный текст недоступен) — точность там ниже
/// принципиально, поэтому текстовый путь предпочтителен всегда, когда доступен.
/// </summary>
public class LmStudioMedicalDocumentExtractor(
    IDocumentTextExtractor documentTextExtractor,
    ILmStudioJsonClient lmStudioClient,
    IOptions<ExtractionOptions> options,
    ILogger<LmStudioMedicalDocumentExtractor> logger) : IMedicalDocumentExtractor
{
    private const int ChunkOverlapChars = 200;

    private const string AnalysisSystemPrompt = """
        Ты — оцифровщик бланков лабораторных анализов. На входе — текст или фото бланка анализа
        (может быть только часть бланка, если документ большой). Извлеки ВСЕ показатели, которые
        реально присутствуют в этом фрагменте, и верни ТОЛЬКО валидный JSON, без пояснений, без
        markdown, без блока <think>.

        Формат ответа:
        {
          "indicators": [
            {
              "name": "название показателя как в бланке (например, \"Гемоглобин\")",
              "value": "значение как напечатано (например, \"118\" или \"отрицательно\")",
              "unit": "единица измерения или null (например, \"г/л\")",
              "refLow": 130,
              "refHigh": 160,
              "refText": "референсный диапазон текстом или null — заполняй ТОЛЬКО если референс НЕ раскладывается на refLow/refHigh (например, \"отрицательно\", \"1-3 в п/зр\")"
            }
          ]
        }

        Правила:
        - Извлекай ТОЛЬКО то, что реально написано в этом фрагменте — ничего не добавляй от себя
          и не переноси показатели из общих знаний о медицине.
        - "refLow"/"refHigh" — числа, только если референс — числовой диапазон (например,
          "130-160"). Если так — "refText" оставь null. Если референс не числовой — заполни
          только "refText", "refLow"/"refHigh" оставь null.
        - Если во фрагменте нет ни одного показателя анализа (это шапка документа, подпись врача,
          пояснительный текст и т.п.) — верни {"indicators": []}.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    private const string VisitSystemPrompt = """
        Ты — оцифровщик заключений и выписок врача. На входе — текст или фото документа (может
        быть только часть документа, если он большой). Извлеки диагноз, рекомендации и назначения
        и верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "diagnosis": "диагноз как указан в документе или null",
          "recommendations": "рекомендации врача или null",
          "prescriptions": "назначенные препараты/процедуры как написано в документе (сырой текст) или null"
        }

        Правила:
        - Заполняй поле только если соответствующая информация реально есть в этом фрагменте —
          иначе null. Не додумывай.
        - "prescriptions" — переноси как написано в документе, не структурируй и не сокращай.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<ExtractionResult> ExtractAsync(DocumentSource source, MedicalRecordKind kind, CancellationToken ct = default)
    {
        var content = await documentTextExtractor.ExtractAsync(source.Content, source.ContentType, ct);
        if (content.Kind == DocumentSourceKind.Unsupported)
        {
            logger.LogInformation(
                "Распознавание «{FileName}» невозможно: {Reason}", source.FileName, content.UnsupportedReason);
            return new ExtractionResult(false, null, null, content.UnsupportedReason);
        }

        return kind == MedicalRecordKind.Analysis
            ? await ExtractAnalysisAsync(content, ct)
            : await ExtractVisitAsync(content, ct);
    }

    private async Task<ExtractionResult> ExtractAnalysisAsync(DocumentContent content, CancellationToken ct)
    {
        var indicators = new List<ExtractedLabIndicator>();

        if (content.Kind == DocumentSourceKind.Text)
        {
            foreach (var chunk in SplitIntoChunks(content.Text!, options.Value.MaxCharsPerChunk, ChunkOverlapChars))
            {
                var result = await lmStudioClient.ExtractJsonAsync(AnalysisSystemPrompt, chunk, ct);
                if (!result.Success || result.Payload is null) continue;

                foreach (var indicator in ParseIndicators(result.Payload))
                {
                    // Антигаллюцинационный гейт (текстовый путь): имя показателя обязано
                    // встречаться в исходном тексте — иначе модель его придумала.
                    if (!chunk.Contains(indicator.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    indicators.Add(indicator);
                }
            }
        }
        else
        {
            foreach (var image in content.Images)
            {
                var result = await lmStudioClient.ExtractJsonAsync(
                    AnalysisSystemPrompt, "Распознай показатели анализа на этом изображении.", [(image.Bytes, image.ContentType)], ct);
                if (!result.Success || result.Payload is null) continue;

                indicators.AddRange(ParseIndicators(result.Payload));
            }
        }

        var deduped = DeduplicateByName(indicators);
        if (deduped.Count == 0)
        {
            return new ExtractionResult(true, [], null, "Не удалось распознать ни одного показателя.");
        }

        return new ExtractionResult(true, deduped, null);
    }

    private async Task<ExtractionResult> ExtractVisitAsync(DocumentContent content, CancellationToken ct)
    {
        if (content.Kind == DocumentSourceKind.Text)
        {
            foreach (var chunk in SplitIntoChunks(content.Text!, options.Value.MaxCharsPerChunk, ChunkOverlapChars))
            {
                var result = await lmStudioClient.ExtractJsonAsync(VisitSystemPrompt, chunk, ct);
                if (!result.Success || result.Payload is null) continue;

                var conclusion = ParseConclusion(result.Payload);
                if (HasContent(conclusion)) return new ExtractionResult(true, null, conclusion);
            }
        }
        else
        {
            foreach (var image in content.Images)
            {
                var result = await lmStudioClient.ExtractJsonAsync(
                    VisitSystemPrompt, "Распознай заключение врача на этом изображении.", [(image.Bytes, image.ContentType)], ct);
                if (!result.Success || result.Payload is null) continue;

                var conclusion = ParseConclusion(result.Payload);
                if (HasContent(conclusion)) return new ExtractionResult(true, null, conclusion);
            }
        }

        return new ExtractionResult(true, null, null, "Не удалось распознать заключение врача.");
    }

    private static IEnumerable<ExtractedLabIndicator> ParseIndicators(Dictionary<string, JsonElement> payload)
    {
        if (!TryGetValue(payload, "indicators", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = ReadString(item, "name")?.Trim();
            var value = ReadString(item, "value")?.Trim();
            // Показатель без имени/значения или с неправдоподобно длинным именем (модель
            // сгенерировала предложение, не название показателя) — отбрасываем.
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value) || name.Length > 80) continue;

            yield return new ExtractedLabIndicator(
                Name: name,
                Value: value,
                Unit: ReadString(item, "unit"),
                RefLow: ReadDouble(item, "refLow"),
                RefHigh: ReadDouble(item, "refHigh"),
                RefText: ReadString(item, "refText"));
        }
    }

    private static VisitConclusion ParseConclusion(Dictionary<string, JsonElement> payload) => new(
        ReadString(payload, "diagnosis"),
        ReadString(payload, "recommendations"),
        ReadString(payload, "prescriptions"));

    private static bool HasContent(VisitConclusion c) =>
        !string.IsNullOrWhiteSpace(c.Diagnosis) || !string.IsNullOrWhiteSpace(c.Recommendations) || !string.IsNullOrWhiteSpace(c.Prescriptions);

    /// <summary>Первое вхождение имени побеждает — куски идут по порядку документа, повторное
    /// упоминание того же показателя дальше в тексте (например, в итоговой таблице после
    /// расшифровки) не должно затирать первое, более контекстное значение.</summary>
    private static List<ExtractedLabIndicator> DeduplicateByName(List<ExtractedLabIndicator> indicators)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExtractedLabIndicator>();
        foreach (var indicator in indicators)
        {
            if (seen.Add(indicator.Name)) result.Add(indicator);
        }
        return result;
    }

    private static IEnumerable<string> SplitIntoChunks(string text, int maxChars, int overlap)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - start);
            yield return text.Substring(start, length);
            if (start + length >= text.Length) yield break;
            start += maxChars - overlap;
        }
    }
}
