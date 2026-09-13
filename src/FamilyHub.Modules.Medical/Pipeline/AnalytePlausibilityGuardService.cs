using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Pipeline;

/// <summary>
/// Дополнительный гейт ТОЛЬКО для показателей, введённых пользователем вручную (см.
/// EnrichmentRequestOrigin.ManualEntry) — отдельный от ILegitimacyGuardService (проверяет
/// prompt injection, не смысл): здесь модель отвечает не "это не инструкция", а "это реально
/// существующий лабораторный показатель, и его сочетание с этим источником имеет смысл". Нужен
/// именно для ручного пути, потому что документное извлечение (LmStudioMedicalDocumentExtractor)
/// уже прошло через собственный антигаллюцинационный гейт (имя показателя обязано встречаться в
/// исходном тексте бланка) — у ручного ввода такой перекрёстной проверки нет вообще, пользователь
/// может ввести любой текст.
///
/// По духу — та же схема, что analysis.specimen-validate (GlobalSpecimenKbService): LLM-вызов,
/// затем прямое чтение "valid"/"reason". В отличие от specimen-validate (fail-open словами
/// "недоступно") здесь deny-by-default, как у LegitimacyGuardService — задача обогащения
/// стоит один платный внешний запрос и пишет в ОБЩИЙ (не персональный) справочник, порог
/// осторожности выше, чем у одноразовой проверки строки при вводе источника.
/// </summary>
public class AnalytePlausibilityGuardService(
    ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<AnalytePlausibilityGuardService> logger)
    : IAnalytePlausibilityGuardService
{
    private const string FallbackPrompt = """
        Ты — валидатор правдоподобности лабораторного показателя, введённого пользователем вручную
        (не распознанного с бланка). На входе — название показателя анализа и, если известен,
        источник/биоматериал, из которого он получен. Оцени: (1) является ли название реально
        существующим лабораторным или клиническим показателем (а не случайным набором слов,
        выдумкой или посторонним текстом), и (2) если источник указан — имеет ли смысл измерять
        ИМЕННО этот показатель именно в этом источнике. Верни ТОЛЬКО валидный JSON, без пояснений,
        без markdown, без блока <think>.

        Формат ответа: {"valid": true, "reason": null}

        Правила:
        - "valid": false — если название не является реально существующим лабораторным/клиническим
          показателем (случайный текст, выдумка, название препарата или источника вместо
          показателя, оскорбление, посторонний контент), ЛИБО если указанное сочетание
          показатель+источник абсурдно (показатель физически не может быть получен из названного
          источника).
        - "valid": true — реальный, пусть даже редкий или узкоспециализированный показатель;
          сомнение в редком, но существующем названии трактуй В ПОЛЬЗУ валидности. Если источник
          не указан (null) — оценивай только само название показателя, без пункта (2).
        - "reason" — короткая причина отказа по-русски при valid=false, иначе null.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<AnalytePlausibilityResult> CheckAsync(
        string analyteName, string? specimenDisplayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(analyteName)) return AnalytePlausibilityResult.Plausible();

        var prompt = await promptProvider.GetAsync("analysis.analyte-plausibility", FallbackPrompt, ct);
        var userText = string.IsNullOrWhiteSpace(specimenDisplayName)
            ? $"Показатель: {analyteName}"
            : $"Показатель: {analyteName}\nИсточник: {specimenDisplayName}";

        // suppressThinking: true — security-гейт, та же причина, что у LegitimacyGuardService.
        var result = await client.ExtractJsonAsync(prompt, userText, ct, suppressThinking: true);
        if (!result.Success || result.Payload is null)
        {
            logger.LogWarning(
                "Проверка правдоподобности показателя технически не удалась ({Error}) — блокируем по умолчанию.",
                result.Error);
            return AnalytePlausibilityResult.Implausible("Проверка правдоподобности временно недоступна.", result.IsTransient);
        }

        if (!TryGetValue(result.Payload, "valid", out var validEl) ||
            (validEl.ValueKind != JsonValueKind.True && validEl.ValueKind != JsonValueKind.False))
        {
            logger.LogWarning("Проверка правдоподобности вернула ответ без поля \"valid\" — блокируем по умолчанию.");
            return AnalytePlausibilityResult.Implausible("Проверка правдоподобности не смогла вынести решение.");
        }

        if (validEl.ValueKind == JsonValueKind.True) return AnalytePlausibilityResult.Plausible();

        var reason = ReadString(result.Payload, "reason");
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "Показатель не прошёл проверку правдоподобности." : reason;
        logger.LogInformation("Проверка правдоподобности отклонила показатель «{Name}»: {Reason}", analyteName, effectiveReason);
        return AnalytePlausibilityResult.Implausible(effectiveReason);
    }
}
