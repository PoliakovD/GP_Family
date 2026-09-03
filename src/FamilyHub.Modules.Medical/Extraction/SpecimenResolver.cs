using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Один "участок" бланка с собственным источником, отличным от общего для документа —
/// бланк с несколькими панелями (например, ОАК + ОАМ на одном листе) даёт несколько таких секций;
/// IndicatorNames — имена показателей ИЗ ЭТОЙ секции, как их назвала модель (сверяются по
/// LabAnalyteNormalizer.Normalize с уже извлечёнными показателями, см. MedicalDocumentExtractionProcessor).</summary>
public record SpecimenSection(string? Context, IReadOnlyList<string> IndicatorNames);

/// <summary>Сырой (ещё не сведённый к строке справочника) итог одного LLM-прохода по документу —
/// см. SpecimenResolver.ResolveAsync.</summary>
public record SpecimenDocumentResolution(
    string? Context, string? RawLabel, string? Evidence, double Confidence, IReadOnlyList<SpecimenSection> Sections)
{
    public static readonly SpecimenDocumentResolution Empty = new(null, null, null, 0, []);
}

/// <summary>
/// Резолвинг ИСТОЧНИКА показателя — один LLM-проход на документ (пересборка enrich-пайплайна).
/// Заменяет прежнее поле "specimen" в промпте структурирования показателей
/// (LmStudioMedicalDocumentExtractor.AnalysisSystemPrompt) — совмещение задач в одном вызове
/// мешало обеим: модель одновременно должна была и вычленять строки таблицы, и классифицировать
/// источник по фиксированному списку токенов. Здесь список токенов исчез вовсе — источник
/// (биоматериал ИЛИ инструментальное исследование вроде ЭКГ/УЗИ, разницы для конвейера нет,
/// см. SpecimenContextIds) описывается моделью свободным текстом и сверяется со справочником
/// (<see cref="GlobalSpecimenKbService"/>) по триграмме — тот же приём "модель предлагает,
/// детерминированный код ветирует", что уже есть в OcrNameCorrector/GlobalSpecimenKbService.
/// </summary>
public class SpecimenResolver(
    ILmStudioJsonClient client, GlobalSpecimenKbService specimenKb, IPromptProvider promptProvider, ILogger<SpecimenResolver> logger)
{
    /// <summary>Ниже этого confidence источник считается нерезолвленным — параметр шага пайплайна
    /// (см. §2 плана, конфигурируется из админки; пока константа).</summary>
    public const double MinConfidence = 0.7;

    /// <summary>Тот же порог, что у OcrNameCorrector/GlobalSpecimenKbService — предложенный
    /// моделью "context" не должен оказаться другим понятием, чем то, что реально написано в
    /// документе ("rawLabel").</summary>
    private const double MinRawLabelSimilarity = 0.3;

    /// <summary>Шапка бланка — источник почти всегда упомянут в первых строках/строке заголовка;
    /// не нужно скармливать модели весь документ ради одного слова.</summary>
    private const int HeaderChars = 2000;

    private const string SystemPrompt = """
        Ты — классификатор источника медицинского анализа. На входе — текст (может быть частью
        документа) или фото бланка. Источник — то, откуда получен показатель: ЭТО МОЖЕТ БЫТЬ
        биоматериал (кровь, моча, кал, слюна, мазок, ликвор, мокрота, синовиальная жидкость и
        т.п.) ИЛИ вид инструментального исследования, если бланк — не анализ биоматериала, а
        результат прибора (ЭКГ, УЗИ, спирометрия, холтеровское мониторирование, рентген и т.п.) —
        оба рода источника равноценны, не ограничивайся только биоматериалом. Определи источник и
        верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "context": "нормализованное название источника (например, \"кровь\", \"моча\", \"ЭКГ\") или null, если не удалось определить",
          "rawLabel": "как источник назван в документе — цитата или близкий пересказ, или null",
          "evidence": "короткая цитата из документа, где это написано, или null",
          "confidence": 0.0,
          "sections": [
            {
              "context": "источник этой секции",
              "indicatorNames": ["название показателя из этой секции", "..."]
            }
          ]
        }

        Правила:
        - "confidence" — число от 0 до 1, твоя уверенность в определении ИМЕННО источника (не в
          том, что документ вообще медицинский). Источник явно не указан или неоднозначен — низкое
          значение (менее 0.5) и/или null в "context".
        - "context" — короткое литературное название источника, БЕЗ порядковых номеров и лишних
          слов ("кровь", не "1. Кровь из вены"). Не придумывай источник, которого нет в документе.
        - "sections" — заполняй ТОЛЬКО если в этом фрагменте реально есть несколько групп
          показателей с явно РАЗНЫМИ источниками (например, отдельно "Общий анализ крови" и
          "Общий анализ мочи" на одном бланке). Для обычного бланка с одним источником — пустой
          массив, всё определяется полем "context" верхнего уровня.
        - Если во фрагменте нет никаких признаков источника (например, это просто таблица
          показателей без шапки) — "context": null, "confidence": 0, "sections": [].
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<SpecimenDocumentResolution> ResolveAsync(DocumentContent content, CancellationToken ct = default)
    {
        LmStudioJsonResult result;
        if (content.Kind == DocumentSourceKind.Text && !string.IsNullOrEmpty(content.Text))
        {
            var header = content.Text.Length > HeaderChars ? content.Text[..HeaderChars] : content.Text;
            var prompt = await promptProvider.GetAsync("analysis.specimen-resolve", SystemPrompt, ct);
            result = await client.ExtractJsonAsync(prompt, header, ct);
        }
        else if (content.Kind == DocumentSourceKind.Image && content.Images.Count > 0)
        {
            var first = content.Images[0];
            var prompt = await promptProvider.GetAsync("analysis.specimen-resolve", SystemPrompt, ct);
            result = await client.ExtractJsonAsync(
                prompt, "Определи источник показателей на этом изображении.",
                [(first.Bytes, first.ContentType)], ct);
        }
        else
        {
            return SpecimenDocumentResolution.Empty;
        }

        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Резолвинг источника показателя недоступен: {Error}", result.Error);
            return SpecimenDocumentResolution.Empty;
        }

        return new SpecimenDocumentResolution(
            ReadString(result.Payload, "context"),
            ReadString(result.Payload, "rawLabel"),
            ReadString(result.Payload, "evidence"),
            ReadDouble(result.Payload, "confidence") ?? 0,
            ParseSections(result.Payload));
    }

    /// <summary>Детерминированный шаг после LLM-вызова (см. class doc) — превращает свободный
    /// текст в ссылку на справочник, БЕЗ второго LLM-вызова: резолвер уже спросил модель один раз
    /// и получил confidence, второй раз спрашивать нечего (в отличие от ручного ввода —
    /// GlobalSpecimenKbService.ValidateAndRegisterAsync, там нет документа и собственного
    /// confidence). Возвращает SpecimenContextIds.Unresolved, если confidence ниже порога, context
    /// пуст, или предложенный context триграммно не похож на rawLabel (модель подменила понятие).</summary>
    public async Task<Guid> ResolveKbIdAsync(
        string? context, double confidence, string? rawLabel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context) || confidence < MinConfidence) return SpecimenContextIds.Unresolved;

        var normalized = LabAnalyteNormalizer.Normalize(context);
        if (normalized.Length == 0) return SpecimenContextIds.Unresolved;

        // Уже есть в справочнике — сразу используем, второй проверки не нужно (кто-то раньше уже
        // прошёл этот же гейт для того же названия).
        var existing = await specimenKb.FindAsync(normalized, ct);
        if (existing is not null) return existing.Id;

        if (!string.IsNullOrWhiteSpace(rawLabel))
        {
            var similarity = TrigramSimilarity.Similarity(normalized, LabAnalyteNormalizer.Normalize(rawLabel));
            if (similarity < MinRawLabelSimilarity)
            {
                logger.LogWarning(
                    "Резолвинг источника: модель предложила «{Context}» для «{RawLabel}», но схожесть " +
                    "{Similarity:F2} слишком низкая — отклонено.", context, rawLabel, similarity);
                return SpecimenContextIds.Unresolved;
            }
        }

        return await specimenKb.FindOrRegisterAsync(context.Trim(), normalized, ct);
    }

    private static List<SpecimenSection> ParseSections(Dictionary<string, JsonElement> payload)
    {
        if (!TryGetValue(payload, "sections", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var sections = new List<SpecimenSection>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var context = ReadString(item, "context");
            var names = TryGetProperty(item, "indicatorNames", out var namesEl) ? ReadStringArray(namesEl) : [];
            if (string.IsNullOrWhiteSpace(context) && names.Count == 0) continue;

            sections.Add(new SpecimenSection(context, names));
        }
        return sections;
    }
}
