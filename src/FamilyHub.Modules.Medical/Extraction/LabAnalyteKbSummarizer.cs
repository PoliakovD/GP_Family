using System.Text;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Суммаризация веб-сниппетов доверенных лабораторных источников локальным Qwen (ветка
/// medicalrecords, зеркало MedicationSummarizer этапа 4 — тот же антигаллюцинационный гейт:
/// модель обязана сослаться хотя бы на один сниппет, иначе запись в справочник отклоняется).
/// </summary>
public class LabAnalyteKbSummarizer(ILmStudioJsonClient client, ILogger<LabAnalyteKbSummarizer> logger)
{
    private const string SystemPrompt = """
        Ты — медицинский справочный ассистент. На входе — название лабораторного показателя
        (анализа) и пронумерованные сниппеты со страниц проверенных лабораторных справочников РФ.
        Проанализируй ТОЛЬКО переданные сниппеты (ничего не добавляй от себя, никаких знаний "из
        головы") и верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "loincCode": "код LOINC показателя, если явно указан в сниппетах, иначе null",
          "defaultUnit": "обычная единица измерения (г/л, ммоль/л и т.п.) или null",
          "plainExplanation": "что это за показатель — простыми бытовыми словами, без медицинских терминов, или null",
          "whyMeasured": "зачем его измеряют / для диагностики чего используется или null",
          "highMeans": "о чём обычно говорит повышенное значение или null",
          "lowMeans": "о чём обычно говорит пониженное значение или null",
          "refRanges": [{"ageFrom": null, "ageTo": null, "sex": null, "low": 120, "high": 160, "unit": "г/л"}],
          "calculationInstructions": "если норма зависит от веса/роста/срока беременности/фазы цикла и т.п. и НЕ сводится к фиксированным диапазонам — словами, как её вычислить, иначе null",
          "aliases": ["другое название/сокращение показателя", "..."],
          "relatedAnalytes": ["показатель, который обычно смотрят вместе с этим", "..."],
          "usedSourceIndexes": [0, 2]
        }

        Правила:
        - "plainExplanation" — коротко и просто, обычными словами, которыми говорят в быту, не
          медицинскими терминами.
        - "refRanges" — один или несколько референсных диапазонов. Если источник даёт диапазоны
          отдельно для мужчин и женщин — верни ДВА объекта с разным "sex" ("male"/"female"), НЕ
          объединяй их в один средний диапазон. Если пол не важен — один объект с "sex": null.
          Если источник даёт диапазоны для разных возрастных групп — несколько объектов с
          ageFrom/ageTo (годы, включительно), можно сочетать с sex (например, отдельно
          "male"+0-1 год и "male"+больше 1 года). Диапазон без возрастных ограничений —
          ageFrom=null, ageTo=null. Если числового диапазона в сниппетах нет вовсе (только
          методика расчёта) — верни пустой массив, а методику опиши в "calculationInstructions".
        - "calculationInstructions" — ТОЛЬКО когда норма реально не выражается фиксированными
          диапазонами (явная формула/зависимость от веса, роста, срока беременности, площади
          поверхности тела и т.п. — например, клиренс креатинина). Для подавляющего большинства
          показателей (гемоглобин, глюкоза и т.д.) это поле — null, у них обычный фиксированный
          диапазон в "refRanges".
        - "aliases" — другие названия/сокращения ТОГО ЖЕ показателя, встреченные в сниппетах
          (например, "Hb", "HGB" для гемоглобина). Пустой массив, если не встречались.
        - "relatedAnalytes" — 2-5 ДРУГИХ показателей (не синонимов этого же), которые лаборатории
          и врачи обычно интерпретируют вместе с этим (например, для гемоглобина — ферритин,
          железо сыворотки, эритроциты, гематокрит), ТОЛЬКО если это явно следует из сниппетов —
          не придумывай по общим медицинским знаниям. Пустой массив, если сниппеты не называют
          связанные показатели.
        - "usedSourceIndexes" — индексы (из подписи "[N]" перед каждым сниппетом) источников, на
          которые реально опирается ответ. Если ни один сниппет не содержит полезной информации о
          показателе — верни пустой массив и null во всех текстовых полях, пустые массивы в refRanges/aliases.
        - Каждое текстовое поле — по существу, без искусственного сокращения и без дословного
          копирования целого сниппета.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<LabAnalyteSummarizeResult> SummarizeAsync(
        string displayName, IReadOnlyList<WebSnippet> snippets, CancellationToken ct = default)
    {
        if (snippets.Count == 0)
            return LabAnalyteSummarizeResult.Failure("Нет сниппетов от доверенных источников — суммаризировать нечего.");

        var userText = BuildUserText(displayName, snippets);
        var result = await client.ExtractJsonAsync(SystemPrompt, userText, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Суммаризация показателя «{DisplayName}» не удалась: {Error}", displayName, result.Error);
            return LabAnalyteSummarizeResult.Failure(result.Error ?? "Модель не вернула структурированный ответ.");
        }

        var usedIndexes = ReadIndexArray(result.Payload, "usedSourceIndexes")
            .Where(i => i >= 0 && i < snippets.Count)
            .Distinct()
            .ToList();

        // Антигаллюцинационный гейт — та же логика, что MedicationSummarizer (см. task-5.1-medications.md).
        if (usedIndexes.Count == 0)
        {
            logger.LogInformation(
                "Суммаризация показателя «{DisplayName}»: модель не сослалась ни на один источник — запись в справочник отклонена.",
                displayName);
            return LabAnalyteSummarizeResult.Failure("Модель не смогла подтвердить ответ ни одним источником.");
        }

        var refRanges = ReadRefRanges(result.Payload);
        var aliases = ReadStringArray(result.Payload, "aliases");
        var calculationInstructions = ReadString(result.Payload, "calculationInstructions");
        var relatedAnalytes = ReadStringArray(result.Payload, "relatedAnalytes");

        var summary = new LabAnalyteSummary(
            LoincCode: ReadString(result.Payload, "loincCode"),
            DefaultUnit: ReadString(result.Payload, "defaultUnit"),
            PlainExplanation: ReadString(result.Payload, "plainExplanation"),
            WhyMeasured: ReadString(result.Payload, "whyMeasured"),
            HighMeans: ReadString(result.Payload, "highMeans"),
            LowMeans: ReadString(result.Payload, "lowMeans"),
            RefRanges: refRanges,
            Aliases: aliases,
            UsedSourceIndexes: usedIndexes,
            CalculationInstructions: calculationInstructions,
            RelatedAnalytes: relatedAnalytes);

        var hasContent = refRanges.Count > 0 || aliases.Count > 0 || !string.IsNullOrWhiteSpace(calculationInstructions) || new[]
        {
            summary.LoincCode, summary.DefaultUnit, summary.PlainExplanation, summary.WhyMeasured,
            summary.HighMeans, summary.LowMeans,
        }.Any(f => !string.IsNullOrWhiteSpace(f));

        if (!hasContent)
        {
            logger.LogInformation("Суммаризация показателя «{DisplayName}»: все поля пусты — запись в справочник отклонена.", displayName);
            return LabAnalyteSummarizeResult.Failure("Модель не извлекла ни одного содержательного поля.");
        }

        return LabAnalyteSummarizeResult.Ok(summary);
    }

    private static List<LabAnalyteReferenceRange> ReadRefRanges(Dictionary<string, System.Text.Json.JsonElement> payload)
    {
        if (!TryGetValue(payload, "refRanges", out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var ranges = new List<LabAnalyteReferenceRange>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

            var low = ReadDouble(item, "low");
            var high = ReadDouble(item, "high");
            if (low is null && high is null) continue; // диапазон без чисел бесполезен

            var ageFrom = ReadDouble(item, "ageFrom");
            var ageTo = ReadDouble(item, "ageTo");
            ranges.Add(new LabAnalyteReferenceRange(
                AgeFrom: ageFrom.HasValue ? (int)ageFrom.Value : null,
                AgeTo: ageTo.HasValue ? (int)ageTo.Value : null,
                Sex: ParseSex(ReadString(item, "sex")),
                Low: low, High: high,
                Unit: ReadString(item, "unit")));
        }
        return ranges;
    }

    /// <summary>Регистронезависимо — модель иногда меняет casing/язык ("Male"/"м"/"муж").</summary>
    private static Gender? ParseSex(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "male" or "m" or "муж" or "мужской" => Gender.Male,
        "female" or "f" or "жен" or "женский" => Gender.Female,
        _ => null,
    };

    private static string BuildUserText(string displayName, IReadOnlyList<WebSnippet> snippets)
    {
        var sb = new StringBuilder();
        sb.Append("Показатель: ").AppendLine(displayName).AppendLine();
        for (var i = 0; i < snippets.Count; i++)
        {
            var s = snippets[i];
            sb.Append('[').Append(i).Append("] ").AppendLine(s.Title);
            sb.AppendLine(s.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
