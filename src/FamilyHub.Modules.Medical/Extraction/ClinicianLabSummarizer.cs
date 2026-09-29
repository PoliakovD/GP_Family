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
/// Сводка анализа для ВРАЧА (план "качество ИИ-распознавания анализов", Этап 4) — отдельный вызов
/// от <see cref="LabSummarizer"/> (пациентской, видимой в самой записи), не общий текст для обоих
/// читателей: то, что удобно пациенту (просто, без терминов, с оговорками), для врача избыточно и
/// медленно читается, а то, что нужно врачу (плотно, клиническим языком, без разжёвывания) пугающе
/// выглядело бы для пациента. Эта сводка НИГДЕ не показывается пациенту — только в отчёте врачу
/// (см. DoctorReportDataCollector/DoctorReportHtmlRenderer), хранится в MedicalRecord.
/// ClinicianSummaryJson отдельно от пациентского MedicalRecord.SummaryJson.
///
/// Тот же антигаллюцинационный гейт, что LabSummarizer (usedIndicatorNames обязаны ссылаться на
/// реально переданные показатели) — независимая копия проверки, не общий метод: два разных набора
/// полей ответа (deviation.meaning vs deviation.clinical), делить код ради экономии нескольких строк
/// было бы связкой двух разных промптов через одну хрупкую сигнатуру.
/// </summary>
public class ClinicianLabSummarizer(ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<ClinicianLabSummarizer> logger)
{
    private const string SystemPrompt = """
        Ты — ассистент, готовящий сжатую клиническую сводку анализа ДЛЯ ВРАЧА (не для пациента) —
        часть выписки, которую врач читает быстро перед приёмом. На входе — список показателей
        анализа (название, значение, единица, референс, отклонение от нормы, если есть) и, если
        известны, возраст и пол пациента. Верни ТОЛЬКО валидный JSON, без пояснений, без markdown,
        без блока <think>.

        Формат ответа:
        {
          "overview": "1-2 предложения клиническим языком — общая картина по переданным показателям",
          "deviations": [
            { "name": "название показателя с отклонением", "clinical": "значение, референс, направление и выраженность отклонения клиническим языком — плотно, для быстрого чтения врачом, без разжёвывания" }
          ],
          "dataQualityNote": "короткое замечание о качестве данных, если это важно для интерпретации (например, часть норм — оценка ИИ, а не значение с бланка или проверенного справочника), иначе null",
          "usedIndicatorNames": ["название показателя 1", "название показателя 2"]
        }

        Правила:
        - Пиши для врача: медицинские термины и сокращения уместны, не разжёвывай как для пациента
          (не нужны фразы вроде "это может означать" или оговорки про поход к врачу).
        - "deviations" — только показатели, реально помеченные как отклонение от нормы во входных
          данных. Если отклонений нет — пустой массив, "overview" должен это отражать.
        - НЕ ставь диагноз и не назначай лечение — только сжатое, но содержательное описание того,
          что отклонилось и насколько.
        - "usedIndicatorNames" — имена показателей (ровно как во входных данных), на основе которых
          построена сводка. Если не удалось проанализировать ни один показатель — пустые массивы.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<ClinicianLabSummaryResult> SummarizeAsync(
        IReadOnlyList<LabIndicator> indicators, int? ageYears, Gender? sex, CancellationToken ct = default)
    {
        if (indicators.Count == 0) return ClinicianLabSummaryResult.Failure("Нет показателей для суммаризации.");

        var userText = BuildUserText(indicators, ageYears, sex);
        var prompt = await promptProvider.GetAsync("analysis.record-summary.clinician", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, userText, ct, shortTimeout: true);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Клиническая суммаризация анализа не удалась: {Error}", result.Error);
            return ClinicianLabSummaryResult.Failure(result.Error ?? "Модель не вернула структурированный ответ.", result.IsTransient);
        }

        var usedNames = ReadStringArray(result.Payload, "usedIndicatorNames");
        var knownNames = indicators.Select(i => i.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var validUsedNames = usedNames.Where(knownNames.Contains).ToList();
        if (validUsedNames.Count == 0)
        {
            logger.LogInformation("Клиническая суммаризация анализа: модель не сослалась ни на один реальный показатель — отклонено.");
            return ClinicianLabSummaryResult.Failure("Модель не смогла подтвердить сводку ни одним показателем.");
        }

        var deviations = ReadDeviations(result.Payload, knownNames);
        var overview = ReadString(result.Payload, "overview");
        var dataQualityNote = ReadString(result.Payload, "dataQualityNote");

        if (string.IsNullOrWhiteSpace(overview) && deviations.Count == 0)
        {
            return ClinicianLabSummaryResult.Failure("Модель не извлекла ни одного содержательного поля.");
        }

        return ClinicianLabSummaryResult.Ok(new ClinicianLabSummary(overview, deviations, dataQualityNote));
    }

    private static List<ClinicianLabSummaryDeviation> ReadDeviations(Dictionary<string, JsonElement> payload, HashSet<string> knownNames)
    {
        if (!TryGetValue(payload, "deviations", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        var result = new List<ClinicianLabSummaryDeviation>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = ReadString(item, "name")?.Trim();
            var clinical = ReadString(item, "clinical")?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(clinical)) continue;
            if (!knownNames.Contains(name)) continue;
            result.Add(new ClinicianLabSummaryDeviation(name, clinical));
        }
        return result;
    }

    private static string BuildUserText(IReadOnlyList<LabIndicator> indicators, int? ageYears, Gender? sex)
    {
        var sb = new StringBuilder();
        if (ageYears is not null || sex is not null)
        {
            sb.Append("Пациент: ");
            if (ageYears is not null) sb.Append(ageYears).Append(" лет");
            if (ageYears is not null && sex is not null) sb.Append(", ");
            if (sex is not null) sb.Append(sex == Gender.Male ? "мужской пол" : "женский пол");
            sb.AppendLine();
        }

        foreach (var i in indicators)
        {
            sb.Append("- ").Append(i.DisplayName).Append(": ").Append(i.ValueRaw);
            if (!string.IsNullOrEmpty(i.Unit)) sb.Append(' ').Append(i.Unit);
            if (!string.IsNullOrEmpty(i.RefText)) sb.Append(" (референс: ").Append(i.RefText).Append(')');
            else if (i.RefLowText is not null || i.RefHighText is not null)
                sb.Append(" (референс: ").Append(i.RefLowText).Append('-').Append(i.RefHighText).Append(')');
            sb.Append(" — ").Append(FlagText(i.Flag));
            if (i.RefSource == RefSource.Inferred) sb.Append(" [норма — оценка ИИ, не с бланка]");
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

public record ClinicianLabSummaryDeviation(string Name, string Clinical);

public record ClinicianLabSummary(string? Overview, IReadOnlyList<ClinicianLabSummaryDeviation> Deviations, string? DataQualityNote);

/// <summary>IsTransient — та же семантика, что LabSummaryResult (см. его докстринг).</summary>
public record ClinicianLabSummaryResult(bool Success, ClinicianLabSummary? Summary, string? Error, bool IsTransient = false)
{
    public static ClinicianLabSummaryResult Ok(ClinicianLabSummary summary) => new(true, summary, null);
    public static ClinicianLabSummaryResult Failure(string error, bool isTransient = false) => new(false, null, error, isTransient);
}
