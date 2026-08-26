using System.Text;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.LmStudio;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Шаг 3 каскада референса (RefSource.KbCalculated) — когда в GlobalLabAnalyteKb для показателя
/// нет подходящего фиксированного диапазона (PickBestRange не совпал), но есть
/// CalculationInstructions (методика словами — например, формула клиренса креатинина), локальная
/// LLM считает low/high конкретно под этого пациента. Возраст/пол — из профиля (identity rework);
/// вес/рост и другие факторы из методики намеренно НЕ передаются (профильных полей под них нет —
/// решение при планировании ветки medicalrecords v2: доводить до полноценных мед-параметров с
/// историей значений — отдельная задача вне объёма). Единица измерения ответа обязана буквально
/// совпасть с той, что напечатана в бланке — иначе результат отбрасывается: смешать единицы
/// опаснее, чем не посчитать вовсе.
/// </summary>
public class PatientReferenceCalculator(ILmStudioJsonClient client, ILogger<PatientReferenceCalculator> logger)
{
    private const string SystemPrompt = """
        Ты — медицинский калькулятор референсных значений. На входе — название лабораторного
        показателя, словесная методика расчёта его нормы и данные пациента. Посчитай числовой
        референсный диапазон (low/high) для ЭТОГО пациента, СТРОГО в указанной единице измерения,
        и верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа: {"low": 60, "high": 120, "unit": "мл/мин"}

        Правила:
        - "unit" в ответе ДОЛЖЕН быть той же единицей, что указана в запросе как требуемая. Если
          не можешь пересчитать методику в эту единицу — верни {"low": null, "high": null, "unit": null}.
        - Если данных пациента недостаточно, чтобы применить методику (например, она требует вес,
          а веса нет в запросе) — верни {"low": null, "high": null, "unit": null}, не угадывай и
          не подставляй среднестатистические значения вместо отсутствующих данных.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<(double Low, double High)?> CalculateAsync(
        string analyteName, string calculationInstructions, int? ageYears, Gender? sex, string? unit, CancellationToken ct = default)
    {
        var userText = BuildUserText(analyteName, calculationInstructions, ageYears, sex, unit);
        var result = await client.ExtractJsonAsync(SystemPrompt, userText, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Расчёт референса «{Name}» не удался: {Error}", analyteName, result.Error);
            return null;
        }

        var low = ReadNumber(result.Payload, "low");
        var high = ReadNumber(result.Payload, "high");
        if (low is null || high is null) return null;

        var returnedUnit = ReadString(result.Payload, "unit");
        if (!string.IsNullOrWhiteSpace(unit) && !UnitsMatch(unit, returnedUnit))
        {
            logger.LogInformation(
                "Расчёт референса «{Name}»: единица измерения не совпала ({Expected} vs {Actual}) — отклонено.",
                analyteName, unit, returnedUnit);
            return null;
        }

        return (low.Value, high.Value);
    }

    private static double? ReadNumber(Dictionary<string, System.Text.Json.JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el)) return null;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when el.TryGetDouble(out var d) => d,
            System.Text.Json.JsonValueKind.String when double.TryParse(
                el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool UnitsMatch(string expected, string? actual) =>
        actual is not null && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildUserText(string analyteName, string instructions, int? ageYears, Gender? sex, string? unit)
    {
        var sb = new StringBuilder();
        sb.Append("Показатель: ").AppendLine(analyteName);
        sb.Append("Методика расчёта нормы: ").AppendLine(instructions);
        sb.Append("Возраст пациента (лет): ").AppendLine(ageYears?.ToString() ?? "неизвестен");
        sb.Append("Пол пациента: ").AppendLine(sex switch
        {
            Gender.Male => "мужской",
            Gender.Female => "женский",
            _ => "неизвестен",
        });
        sb.Append("Требуемая единица измерения ответа: ").Append(unit ?? "любая подходящая, укажи явно в ответе");
        return sb.ToString();
    }
}
