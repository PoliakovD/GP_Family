using System.Text.Json;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using FamilyHub.Modules.Medical.Pipeline;
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
    SpecimenResolver specimenResolver,
    IPromptProvider promptProvider,
    IPipelineConfigService pipelineConfig,
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
              "name": "название показателя БЕЗ порядкового номера пункта бланка и БЕЗ единицы измерения (например, \"Гемоглобин\", не \"1. Гемоглобин, г/л\")",
              "value": "значение как напечатано (например, \"118\" или \"отрицательно\")",
              "unit": "единица измерения или null (например, \"г/л\")",
              "refLow": 130,
              "refHigh": 160,
              "refText": "референсный диапазон текстом или null — заполняй ТОЛЬКО если референс НЕ раскладывается на refLow/refHigh (например, \"отрицательно\", \"1-3 в п/зр\")"
            }
          ],
          "documentDate": "дата анализа/забора материала, как указана в бланке, в формате YYYY-MM-DD, или null",
          "suggestedTitle": "короткое название анализа, если оно прямо напечатано в шапке бланка (например, \"Общий анализ крови\", \"Биохимический анализ крови\") — иначе null, не придумывай",
          "doctor": "ФИО и/или специальность врача, назначившего анализ, если указаны в бланке — иначе null, не придумывай"
        }

        Правила:
        - Извлекай ТОЛЬКО то, что реально написано в этом фрагменте — ничего не добавляй от себя
          и не переноси показатели из общих знаний о медицине.
        - "name" — название показателя БЕЗ порядкового номера строки/пункта бланка ("1.", "12)" и
          т.п. в начале — это нумерация бланка, не часть названия) и без единицы измерения (она
          отдельным полем "unit"). Регистр — как обычно пишут в литературном тексте (с заглавной
          буквы), даже если в бланке весь текст напечатан КАПСОМ.
        - Если в строке бланка нет значения (пустая ячейка, только название показателя без цифры
          или текста напротив, ЛИБО там стоит только прочерк "-"/"—") — НЕ включай этот показатель
          в ответ вообще, пропусти его: прочерк ничего не говорит о результате анализа, хранить
          его бессмысленно. Если же в бланке явно написано СЛОВОМ "отсутствуют", "не обнаружено"
          или "отрицательно" — это осмысленный качественный РЕЗУЛЬТАТ анализа, а не пустая ячейка:
          включай показатель с ним как есть.
        - "refLow"/"refHigh" — числа, только если референс — числовой диапазон (например,
          "130-160"). Если так — "refText" оставь null. Если референс не числовой — заполни
          только "refText", "refLow"/"refHigh" оставь null.
        - "documentDate"/"suggestedTitle"/"doctor" — заполняй, только если это ДЕЙСТВИТЕЛЬНО есть
          в этом фрагменте (обычно в шапке документа); если фрагмент — просто таблица показателей
          без шапки, оставь все три null.
        - Если во фрагменте нет ни одного показателя анализа (это шапка документа, подпись врача,
          пояснительный текст и т.п.) — indicators пустой массив, но documentDate/suggestedTitle
          всё равно заполни, если они есть в этом фрагменте.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    private const string VisitSystemPrompt = """
        Ты — оцифровщик заключений и выписок врача. На входе — текст или фото документа (может
        быть только часть документа, если он большой). Извлеки структурированное содержимое приёма
        и верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "diagnosis": "диагноз как указан в документе или null",
          "anamnesis": "анамнез — жалобы, история заболевания со слов пациента, если записаны врачом, или null",
          "proceduresPerformed": "манипуляции/анализы, выполненные ПРЯМО НА ЭТОМ приёме (осмотр, измерения, взятые пробы и т.п.) или null",
          "recommendations": "рекомендации врача (немедикаментозные — режим, диета, повторный визит и т.п.) или null",
          "prescriptions": [
            {
              "name": "название назначенного препарата как написано в документе",
              "dosageInstructions": "как принимать — доза, кратность, длительность, как написано в документе, или null, если не указано"
            }
          ],
          "documentDate": "дата приёма/выписки, как указана в документе, в формате YYYY-MM-DD, или null",
          "suggestedTitle": "короткое название документа, если оно прямо напечатано (например, \"Выписка невролога\") — иначе null, не придумывай",
          "doctor": "ФИО и/или специальность принимавшего врача, если указаны в документе — иначе null, не придумывай"
        }

        Правила:
        - Заполняй поле только если соответствующая информация реально есть в этом фрагменте —
          иначе null (для "prescriptions" — пустой массив). Не додумывай.
        - "prescriptions" — только препараты, реально НАЗНАЧЕННЫЕ в этом документе, не путай с
          "anamnesis" (что пациент уже принимал раньше) или "proceduresPerformed".
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

        // Поля уровня документа (documentDate/suggestedTitle/doctor) обычно есть только в ШАПКЕ
        // бланка — первый чанк/страница, где модель их реально нашла, побеждает; остальные куски
        // (таблица показателей без шапки) просто не заполняют эти поля повторно. Источник
        // показателя (биоматериал/исследование) сюда больше не входит — резолвится отдельным
        // проходом (см. SpecimenResolver), не как побочное поле промпта структурирования.
        DateOnly? documentDate = null;
        string? suggestedTitle = null;
        string? doctor = null;

        void CaptureDocumentFields(Dictionary<string, JsonElement> payload)
        {
            documentDate ??= ParseDate(ReadString(payload, "documentDate"));
            suggestedTitle ??= ReadString(payload, "suggestedTitle");
            doctor ??= ReadString(payload, "doctor");
        }

        var analysisPrompt = await promptProvider.GetAsync("analysis.extract", AnalysisSystemPrompt, ct);

        if (content.Kind == DocumentSourceKind.Text)
        {
            foreach (var chunk in SplitIntoChunks(content.Text!, options.Value.MaxCharsPerChunk, ChunkOverlapChars))
            {
                var result = await lmStudioClient.ExtractJsonAsync(analysisPrompt, chunk, ct);
                if (!result.Success || result.Payload is null) continue;

                CaptureDocumentFields(result.Payload);
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
                    analysisPrompt, "Распознай показатели анализа на этом изображении.", [(image.Bytes, image.ContentType)], ct);
                if (!result.Success || result.Payload is null) continue;

                CaptureDocumentFields(result.Payload);
                indicators.AddRange(ParseIndicators(result.Payload));
            }
        }

        // Резолвинг источника — отдельный LLM-вызов на весь этот файл (не на чанк/страницу, не
        // побочное поле промпта структурирования выше, см. SpecimenResolver). Ещё не сведён к
        // ссылке на справочник — это делает MedicalDocumentExtractionProcessor, у которого есть
        // доступ к БД (этот класс — чистый LLM-клиент, без Infrastructure.Persistence).
        // Необязательный шаг (§2 плана) — выключен из админки означает, что показатели этого
        // документа остаются с нерезолвленным источником (SpecimenContextIds.Unresolved,
        // проставляется дальше по конвейеру), а не что модель спрашивается впустую.
        var specimenResolution = await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "specimen-resolve", ct)
            ? await specimenResolver.ResolveAsync(content, ct)
            : SpecimenDocumentResolution.Empty;

        var deduped = DeduplicateByName(indicators);
        if (deduped.Count == 0)
        {
            return new ExtractionResult(
                true, [], null, "Не удалось распознать ни одного показателя.", documentDate, suggestedTitle, doctor,
                specimenResolution);
        }

        return new ExtractionResult(
            true, deduped, null, DocumentDate: documentDate, SuggestedTitle: suggestedTitle, Doctor: doctor,
            SpecimenResolution: specimenResolution);
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;

    private async Task<ExtractionResult> ExtractVisitAsync(DocumentContent content, CancellationToken ct)
    {
        var visitPrompt = await promptProvider.GetAsync("visit.extract", VisitSystemPrompt, ct);

        if (content.Kind == DocumentSourceKind.Text)
        {
            foreach (var chunk in SplitIntoChunks(content.Text!, options.Value.MaxCharsPerChunk, ChunkOverlapChars))
            {
                var result = await lmStudioClient.ExtractJsonAsync(visitPrompt, chunk, ct);
                if (!result.Success || result.Payload is null) continue;

                var conclusion = ParseConclusion(result.Payload);
                if (HasContent(conclusion))
                {
                    return new ExtractionResult(
                        true, null, conclusion,
                        DocumentDate: ParseDate(ReadString(result.Payload, "documentDate")),
                        SuggestedTitle: ReadString(result.Payload, "suggestedTitle"),
                        Doctor: ReadString(result.Payload, "doctor"));
                }
            }
        }
        else
        {
            foreach (var image in content.Images)
            {
                var result = await lmStudioClient.ExtractJsonAsync(
                    visitPrompt, "Распознай заключение врача на этом изображении.", [(image.Bytes, image.ContentType)], ct);
                if (!result.Success || result.Payload is null) continue;

                var conclusion = ParseConclusion(result.Payload);
                if (HasContent(conclusion))
                {
                    return new ExtractionResult(
                        true, null, conclusion,
                        DocumentDate: ParseDate(ReadString(result.Payload, "documentDate")),
                        SuggestedTitle: ReadString(result.Payload, "suggestedTitle"),
                        Doctor: ReadString(result.Payload, "doctor"));
                }
            }
        }

        return new ExtractionResult(true, null, null, "Не удалось распознать заключение врача.");
    }

    /// <summary>Плейсхолдеры "нет данных" — включает голый прочерк: в бланке он означает "поле не
    /// заполнено", а не результат анализа, хранить его незачем — график/тренд по нему всё равно
    /// не построить. Словесные "отсутствуют"/"не обнаружено"/"отрицательно" сюда НЕ входят — это
    /// настоящие качественные результаты.</summary>
    private static readonly HashSet<string> EmptyValuePlaceholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "n/a", "na", "нет данных", "не указано", "неизвестно", "?", ".", "-", "—", "–",
    };

    private static IEnumerable<ExtractedLabIndicator> ParseIndicators(Dictionary<string, JsonElement> payload)
    {
        if (!TryGetValue(payload, "indicators", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = ReadString(item, "name")?.Trim();
            var value = ReadString(item, "value")?.Trim();
            // Показатель без имени/значения, с неправдоподобно длинным именем (модель
            // сгенерировала предложение, не название показателя), или со значением-плейсхолдером
            // "нет данных" вместо реального пропуска ячейки — отбрасываем.
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value) || name.Length > 80) continue;
            if (EmptyValuePlaceholders.Contains(value)) continue;

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
        ReadString(payload, "anamnesis"),
        ReadString(payload, "proceduresPerformed"),
        ParsePrescribedMedications(payload));

    private static List<PrescribedMedication> ParsePrescribedMedications(Dictionary<string, JsonElement> payload)
    {
        if (!TryGetValue(payload, "prescriptions", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var result = new List<PrescribedMedication>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = ReadString(item, "name")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200) continue;
            result.Add(new PrescribedMedication(name, ReadString(item, "dosageInstructions")));
        }
        return result;
    }

    private static bool HasContent(VisitConclusion c) =>
        !string.IsNullOrWhiteSpace(c.Diagnosis) || !string.IsNullOrWhiteSpace(c.Recommendations) ||
        !string.IsNullOrWhiteSpace(c.Anamnesis) || !string.IsNullOrWhiteSpace(c.ProceduresPerformed) ||
        (c.PrescribedMedications is { Count: > 0 });

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
