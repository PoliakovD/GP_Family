using System.Text;
using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Summary анализа простым языком + вопросы врачу (ветка medicalrecords, задача 5.2) — текстовая
/// суммаризация уже СОХРАНЁННЫХ показателей локальным Qwen (без фото). Антигаллюцинационный гейт —
/// тот же приём, что MedicationSummarizer: пустой usedIndicatorNames или ссылка на показатель,
/// которого не было среди переданных, отклоняет всю сводку. Дисклеймер «это не диагноз»
/// подмешивается в ответ константой, не запрашивается у модели — не полагаемся на то, что модель
/// не забудет его вставить.
/// </summary>
public class LabSummarizer(ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<LabSummarizer> logger)
{
    public const string Disclaimer =
        "Это не диагноз и не медицинская рекомендация — только помощь в чтении бланка. Точную трактовку результатов даёт врач.";

    private const string SystemPrompt = """
        Ты — помощник, объясняющий результаты анализов простым языком. На входе — список
        показателей анализа (название, значение, единица, референс, отклонение от нормы, если
        есть). Проанализируй ТОЛЬКО переданные показатели и верни ТОЛЬКО валидный JSON, без
        пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "plainSummary": "2-3 предложения простым языком о том, что в целом показывает анализ",
          "deviations": [
            { "name": "название показателя с отклонением", "meaning": "что это может означать простым языком, без постановки диагноза" }
          ],
          "questionsForDoctor": ["вопрос 1", "вопрос 2"],
          "usedIndicatorNames": ["название показателя 1", "название показателя 2"]
        }

        Правила:
        - "deviations" — только показатели, реально помеченные как отклонение от нормы во входных
          данных. Если отклонений нет — пустой массив, и plainSummary должен об этом сказать.
        - "meaning" — НЕ ставь диагноз и не советуй лечение, только опиши, за что обычно отвечает
          показатель и что может значить отклонение в общих чертах.
        - "questionsForDoctor" — 3-5 конкретных вопросов, которые имеет смысл задать врачу по
          итогам ИМЕННО этих отклонений. Если отклонений нет — можно вернуть пустой массив.
        - "usedIndicatorNames" — имена показателей (ровно как во входных данных), на основе
          которых построена сводка. Если не удалось проанализировать ни один показатель — верни
          пустые массивы во всех полях.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<LabSummaryResult> SummarizeAsync(IReadOnlyList<LabIndicator> indicators, CancellationToken ct = default)
    {
        if (indicators.Count == 0) return LabSummaryResult.Failure("Нет показателей для суммаризации.");

        var userText = BuildUserText(indicators);
        var prompt = await promptProvider.GetAsync("analysis.record-summary", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, userText, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Суммаризация анализа не удалась: {Error}", result.Error);
            return LabSummaryResult.Failure(result.Error ?? "Модель не вернула структурированный ответ.");
        }

        var usedNames = ReadStringArray(result.Payload, "usedIndicatorNames");
        var knownNames = indicators.Select(i => i.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Гейт: пустой список — как у MedicationSummarizer. Дополнительно (специфично для этого
        // конвейера): каждое упомянутое имя обязано быть среди РЕАЛЬНО переданных показателей —
        // модель не должна была придумать показатель, которого не было в этом анализе.
        var validUsedNames = usedNames.Where(knownNames.Contains).ToList();
        if (validUsedNames.Count == 0)
        {
            logger.LogInformation("Суммаризация анализа: модель не сослалась ни на один реальный показатель — отклонено.");
            return LabSummaryResult.Failure("Модель не смогла подтвердить сводку ни одним показателем.");
        }

        var deviations = ReadDeviations(result.Payload, knownNames);
        var plainSummary = ReadString(result.Payload, "plainSummary");
        var questions = ReadStringArray(result.Payload, "questionsForDoctor");

        if (string.IsNullOrWhiteSpace(plainSummary) && deviations.Count == 0 && questions.Count == 0)
        {
            return LabSummaryResult.Failure("Модель не извлекла ни одного содержательного поля.");
        }

        var summary = new LabSummary(plainSummary, deviations, questions, Disclaimer);
        return LabSummaryResult.Ok(summary);
    }

    private static List<LabSummaryDeviation> ReadDeviations(Dictionary<string, JsonElement> payload, HashSet<string> knownNames)
    {
        if (!TryGetValue(payload, "deviations", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var result = new List<LabSummaryDeviation>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = ReadString(item, "name")?.Trim();
            var meaning = ReadString(item, "meaning")?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(meaning)) continue;
            if (!knownNames.Contains(name)) continue; // модель сослалась на показатель, которого не было
            result.Add(new LabSummaryDeviation(name, meaning));
        }
        return result;
    }

    private static string BuildUserText(IReadOnlyList<LabIndicator> indicators)
    {
        var sb = new StringBuilder();
        foreach (var i in indicators)
        {
            sb.Append("- ").Append(i.DisplayName).Append(": ").Append(i.ValueRaw);
            if (!string.IsNullOrEmpty(i.Unit)) sb.Append(' ').Append(i.Unit);
            if (!string.IsNullOrEmpty(i.RefText)) sb.Append(" (референс: ").Append(i.RefText).Append(')');
            else if (i.RefLowText is not null || i.RefHighText is not null)
                sb.Append(" (референс: ").Append(i.RefLowText).Append('-').Append(i.RefHighText).Append(')');
            sb.Append(" — ").Append(FlagText(i.Flag));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FlagText(IndicatorFlag flag) => flag switch
    {
        IndicatorFlag.Low => "ниже нормы",
        IndicatorFlag.High => "выше нормы",
        IndicatorFlag.Critical => "критическое отклонение",
        IndicatorFlag.Normal => "в норме",
        _ => "норма неизвестна",
    };
}

public record LabSummaryDeviation(string Name, string Meaning);

public record LabSummary(string? PlainSummary, IReadOnlyList<LabSummaryDeviation> Deviations, IReadOnlyList<string> QuestionsForDoctor, string Disclaimer);

public record LabSummaryResult(bool Success, LabSummary? Summary, string? Error)
{
    public static LabSummaryResult Ok(LabSummary summary) => new(true, summary, null);
    public static LabSummaryResult Failure(string error) => new(false, null, error);
}
