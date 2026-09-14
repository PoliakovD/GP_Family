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
/// IndicatorFlagCalculator.TryApplyInferred) уже не дало ответа. Живой пример, из-за которого этот
/// шаг появился: бланк без печатного референса, показатель "Микрофлора смешанная... методом
/// световой микроскопии", значение "коккобацилярная, обильно" — явное отклонение, но ни диапазон,
/// ни бинарная полярность здесь не применимы: нужна судейская оценка по смыслу свободного текста
/// (шкалы обильности "+"/"++"/"+++", развёрнутые описания). Короткий прицельный вызов локальной
/// LLM — название показателя (обычно уже содержит методику) + значение + всё, что уже известно
/// (собственная более ранняя догадка модели про ожидаемую норму, пояснения статьи справочника,
/// если KB-запись нашлась) — не изобретаем новый источник данных, просто просим модель СУДИТЬ по
/// уже имеющемуся контексту, а не считать заново.
/// </summary>
public class QualitativeNormJudge(ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<QualitativeNormJudge> logger)
{
    private const string SystemPrompt = """
        Ты — опытный врач лабораторной диагностики. На входе — название лабораторного показателя
        (часто содержит методику исследования) и его результат. Референсный диапазон для этого
        показателя в бланке анализа НЕ напечатан, а результат — описательный или качественный, не
        раскладывается на простое "обнаружено"/"не обнаружено" (например, шкала обильности
        "+"/"++"/"+++", развёрнутое словесное описание вроде "кокки, обильно" в мазке). Определи,
        является ли результат НОРМАЛЬНЫМ (здоровым, не требующим внимания врача) для ЭТОГО
        конкретного показателя, по общемедицинским знаниям. Верни ТОЛЬКО валидный JSON, без
        пояснений, без markdown, без блока <think>.

        Формат ответа: {"isNormal": true, "confidence": 0.8}

        Правила:
        - "isNormal": true — результат в пределах ожидаемой нормы; false — явное отклонение,
          требующее внимания; null — ты не можешь уверенно определить это по названию и значению
          (недостаточно контекста, редкий/неоднозначный показатель) — в этом случае НЕ угадывай,
          верни null.
        - "confidence" — число от 0 до 1, твоя уверенность в ответе (для null-ответа можно 0).
        - Обильность/количество ("++", "обильно", "умеренно", "скудно", "единичные") для многих
          качественных лабораторных результатов ЗНАЧИМА — единичные/скудные находки часто норма,
          обильные/множественные часто отклонение, но это зависит от конкретного показателя, суди
          по своим знаниям, не по универсальному правилу.
        - Если рядом дана "Подсказка" (пояснение из справочника или более ранняя догадка модели о
          норме) — используй её как ориентир, но не следуй слепо, если она явно противоречит
          конкретному значению.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    /// <summary>Null — модель недоступна, ответ не распарсился, либо сама вернула null (не
    /// уверена). Вызывающая сторона в этом случае оставляет прежний (Unknown, None) — угадывать
    /// вместо модели не нужно.</summary>
    public async Task<bool?> JudgeAsync(
        string indicatorName, string value, string? unit, string? modelExpectedNorm, string? kbHint, CancellationToken ct = default)
    {
        var userText = BuildUserText(indicatorName, value, unit, modelExpectedNorm, kbHint);
        var prompt = await promptProvider.GetAsync("analysis.qualitative-judge", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, userText, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Оценка нормы «{Name}» не удалась: {Error}", indicatorName, result.Error);
            return null;
        }

        return ReadBool(result.Payload, "isNormal");
    }

    private static string BuildUserText(string indicatorName, string value, string? unit, string? modelExpectedNorm, string? kbHint)
    {
        var sb = new StringBuilder();
        sb.Append("Показатель: ").AppendLine(indicatorName);
        sb.Append("Результат: ").Append(value);
        if (!string.IsNullOrWhiteSpace(unit)) sb.Append(' ').Append(unit);
        sb.AppendLine();
        // modelExpectedNorm — ExtractedLabIndicator.RefExpected, собственная догадка модели про
        // ожидаемую норму на этапе распознавания (см. IndicatorFlagCalculator.TryApplyInferred) —
        // тому шагу текста не хватило (не раскладывается ни диапазоном, ни полярностью), но как
        // подсказка здесь он всё ещё полезен. kbHint — пояснения статьи справочника (plainExplanation/
        // highMeans/lowMeans), если показатель уже привязан к KB-записи.
        if (!string.IsNullOrWhiteSpace(modelExpectedNorm))
            sb.Append("Подсказка (ожидаемая норма): ").AppendLine(modelExpectedNorm);
        if (!string.IsNullOrWhiteSpace(kbHint))
            sb.Append("Подсказка (из справочника): ").AppendLine(kbHint);
        return sb.ToString();
    }
}
