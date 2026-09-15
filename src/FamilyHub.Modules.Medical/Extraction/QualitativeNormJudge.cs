using System.Globalization;
using System.Text;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Самый дорогой и самый редкий резервный шаг каскада (RefSource.Inferred) — вызывается, только
/// когда ВСЁ остальное (числовой диапазон/censored-значение, полярность "обнаружено"/"не
/// обнаружено" — см. ReferenceRangeTextParser/QualitativeResultClassifier/
/// IndicatorFlagCalculator.TryApplyInferred) уже не дало ответа. Два разных живых примера, из-за
/// которых этот шаг появился:
/// 1. Бланк без печатного референса, показатель "Микрофлора смешанная... методом световой
///    микроскопии", значение "коккобацилярная, обильно" — явное отклонение, но ни диапазон, ни
///    бинарная полярность здесь не применимы.
/// 2. Значение — качественное "не обнаружено"/отсутствие, а известный числовой диапазон (из бланка
///    или справочника) начинается НЕ с нуля (например, "2-10") — механическое "значение ниже
///    диапазона ⇒ Low" здесь ОШИБОЧНО для многих показателей: отсутствие/следовые количества —
///    норма, а не патология. Различить это можно только по HighMeans/LowMeans статьи справочника
///    (что означает повышенный/пониженный результат для ИМЕННО этого показателя), не по самим
///    числам — поэтому они передаются модели РАЗДЕЛЬНО, не одной склеенной строкой.
///
/// Короткий прицельный вызов локальной LLM — название показателя (обычно уже содержит методику) +
/// значение + весь уже известный контекст (границы диапазона, если есть; собственная более ранняя
/// догадка модели про ожидаемую норму; пояснения статьи справочника, если показатель уже привязан
/// к KB) — не изобретаем новый источник данных, просто просим модель СУДИТЬ по уже имеющемуся
/// контексту, а не считать заново.
/// </summary>
public class QualitativeNormJudge(ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<QualitativeNormJudge> logger)
{
    private const string SystemPrompt = """
        Ты — опытный врач лабораторной диагностики. На входе — название лабораторного показателя
        (часто содержит методику исследования), его результат и весь уже известный контекст
        (референсный диапазон, если он где-то есть; что означают повышенный/пониженный результат
        именно для этого показателя, если это известно). Определи, является ли результат
        НОРМАЛЬНЫМ (здоровым, не требующим внимания врача) для ЭТОГО конкретного показателя. Верни
        ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа: {"isNormal": true, "confidence": 0.8}

        Правила:
        - "isNormal": true — результат в пределах ожидаемой нормы; false — явное отклонение,
          требующее внимания; null — ты не можешь уверенно определить это по данному контексту
          (недостаточно данных, редкий/неоднозначный показатель) — в этом случае НЕ угадывай,
          верни null.
        - "confidence" — число от 0 до 1, твоя уверенность в ответе (для null-ответа можно 0).
        - Если дан референсный диапазон — НЕ считай механически, что "значение вне диапазона" всегда
          означает отклонение. В частности, отсутствие/следовое количество показателя (значения
          вроде "не обнаружено", "отсутствует", "0") НИЖЕ нижней границы диапазона (например,
          диапазон "2-10", значение отсутствует) для МНОГИХ показателей — здоровая норма, а не
          патология: используй "Что означает пониженный результат" (если дано), чтобы понять,
          действительно ли это отклонение именно для этого показателя, или норма.
        - Обильность/количество ("++", "обильно", "умеренно", "скудно", "единичные") для многих
          качественных лабораторных результатов ЗНАЧИМА — единичные/скудные находки часто норма,
          обильные/множественные часто отклонение, но это зависит от конкретного показателя, суди
          по своим знаниям, не по универсальному правилу.
        - Если рядом дана "Подсказка" (более ранняя догадка модели о норме) — используй её как
          ориентир, но не следуй слепо, если она явно противоречит конкретному значению.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    /// <summary>Null — модель недоступна, ответ не распарсился, либо сама вернула null (не
    /// уверена). Вызывающая сторона в этом случае оставляет прежний результат — угадывать вместо
    /// модели не нужно.</summary>
    public async Task<bool?> JudgeAsync(
        string indicatorName, string value, string? unit, string? modelExpectedNorm,
        double? refLow, double? refHigh, LabAnalyteKbPayload.KbNormExplanations? kbNorm,
        CancellationToken ct = default)
    {
        var userText = BuildUserText(indicatorName, value, unit, modelExpectedNorm, refLow, refHigh, kbNorm);
        var prompt = await promptProvider.GetAsync("analysis.qualitative-judge", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, userText, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Оценка нормы «{Name}» не удалась: {Error}", indicatorName, result.Error);
            return null;
        }

        return ReadBool(result.Payload, "isNormal");
    }

    private static string BuildUserText(
        string indicatorName, string value, string? unit, string? modelExpectedNorm,
        double? refLow, double? refHigh, LabAnalyteKbPayload.KbNormExplanations? kbNorm)
    {
        var sb = new StringBuilder();
        sb.Append("Показатель: ").AppendLine(indicatorName);
        sb.Append("Результат: ").Append(value);
        if (!string.IsNullOrWhiteSpace(unit)) sb.Append(' ').Append(unit);
        sb.AppendLine();

        // Границы известны (из бланка или справочника), даже если сам результат — качественный
        // текст, который под них формально не подставить (см. class doc, пример 2) — модель
        // должна видеть их как ОРИЕНТИР, не как готовый вердикт.
        if (refLow is not null || refHigh is not null)
        {
            sb.Append("Известный референсный диапазон: ")
                .Append(refLow?.ToString(CultureInfo.InvariantCulture) ?? "?")
                .Append('-')
                .AppendLine(refHigh?.ToString(CultureInfo.InvariantCulture) ?? "?");
        }

        // HighMeans/LowMeans — РАЗДЕЛЬНО, не одной строкой (см. class doc) — модель должна знать,
        // какое пояснение относится к повышенному результату, а какое — к пониженному/отсутствию.
        if (kbNorm is not null)
        {
            if (!string.IsNullOrWhiteSpace(kbNorm.HighMeans))
                sb.Append("Что означает ПОВЫШЕННЫЙ результат для этого показателя: ").AppendLine(kbNorm.HighMeans);
            if (!string.IsNullOrWhiteSpace(kbNorm.LowMeans))
                sb.Append("Что означает ПОНИЖЕННЫЙ (или отсутствующий) результат для этого показателя: ").AppendLine(kbNorm.LowMeans);
            if (!string.IsNullOrWhiteSpace(kbNorm.PlainExplanation))
                sb.Append("Общее пояснение показателя: ").AppendLine(kbNorm.PlainExplanation);
        }

        // modelExpectedNorm — ExtractedLabIndicator.RefExpected, собственная догадка модели про
        // ожидаемую норму на этапе распознавания (см. IndicatorFlagCalculator.TryApplyInferred) —
        // тому шагу текста не хватило, но как подсказка здесь он всё ещё полезен.
        if (!string.IsNullOrWhiteSpace(modelExpectedNorm))
            sb.Append("Подсказка (ожидаемая норма): ").AppendLine(modelExpectedNorm);

        return sb.ToString();
    }
}
