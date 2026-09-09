using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Сырой (ещё не сведённый к строке справочника) итог одного LLM-прохода по документу —
/// см. SpecimenResolver.ResolveAsync. Источник — атрибут ВСЕГО документа (заметка 1: один анализ,
/// один источник; посекционный разбор одного бланка на разные источники убран — смешанный бланк
/// пользователь разделяет вручную на несколько записей), поэтому здесь больше нет списка секций.
/// <see cref="NeedsSite"/> — заметка 2: источник, бессмысленный без локализации (мазок, соскоб),
/// упомянут в документе БЕЗ уточнения места — Context в этом случае null (не регистрируем голое
/// слово в справочнике), а NeedsSite несёт обобщённое слово для UI-подсказки "уточните источник".</summary>
public record SpecimenDocumentResolution(
    string? Context, string? RawLabel, string? Evidence, double Confidence, string? NeedsSite)
{
    public static readonly SpecimenDocumentResolution Empty = new(null, null, null, 0, null);
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
        биоматериал (кровь, моча, кал, слюна, мазок, ликвор, мокрота, синовиальная жидкость,
        эякулят и т.п.) ИЛИ вид инструментального исследования, если бланк — не анализ биоматериала,
        а результат прибора (ЭКГ, УЗИ, спирометрия, холтеровское мониторирование, рентген и т.п.) —
        оба рода источника равноценны, не ограничивайся только биоматериалом. Весь документ —
        ОДИН источник (даже если в нём несколько таблиц/разделов показателей). Определи источник и
        верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "context": "нормализованное название источника (например, \"кровь\", \"моча\", \"эякулят\", \"ЭКГ\", \"вагинальный мазок\") или null",
          "rawLabel": "как источник назван в документе — цитата или близкий пересказ, или null",
          "evidence": "короткая цитата из документа, где это написано, или null",
          "confidence": 0.0,
          "needsSite": "обобщённое слово источника без уточнения места (например, \"мазок\", \"соскоб\"), если место не указано в документе — иначе null"
        }

        Правила:
        - "confidence" — число от 0 до 1, твоя уверенность в определении ИМЕННО источника (не в
          том, что документ вообще медицинский). Источник явно не указан или неоднозначен — низкое
          значение (менее 0.5) и/или null в "context".
        - "context" — КОРОТКОЕ литературное название ТОЛЬКО самого источника/биоматериала/
          исследования: "кровь", "моча", "эякулят", "ЭКГ". НИКОГДА не включай в "context" название
          раздела бланка, вида исследования этого раздела или методики — "Физические свойства",
          "Микроскопическое исследование", "Биохимический анализ", "Общий анализ" и подобные слова
          ЭТО НЕ ИСТОЧНИК, а заголовок таблицы внутри источника — если видишь "Микроскопическое
          исследование эякулята", "context" должен быть "эякулят", а не вся фраза целиком.
        - Некоторые источники бессмысленны без указания МЕСТА (мазок, соскоб, пунктат, биоптат,
          отделяемое) — если документ ТОЧНО называет место (например "вагинальный мазок", "мазок
          из зева", "ректальный мазок", "уретральный соскоб") — включи место прямо в "context"
          целиком, как одно понятие. Если же источник такого рода назван ТОЛЬКО обобщённо, без
          места ("мазок" без уточнения, просто "соскоб") — верни "context": null, "confidence": 0,
          а обобщённое слово положи в "needsSite" (например "мазок") — само по себе оно не
          является достаточным источником и не должно попасть в справочник как есть.
        - Не придумывай источник, которого нет в документе.
        - Если во фрагменте нет никаких признаков источника (например, это просто таблица
          показателей без шапки) — "context": null, "confidence": 0, "needsSite": null.
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
            ReadString(result.Payload, "needsSite"));
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
}
