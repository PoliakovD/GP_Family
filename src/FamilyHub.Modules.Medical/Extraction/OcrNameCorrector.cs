using System.Text;
using System.Text.Json;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Второй, отдельный проход локальной LLM по уже извлечённым названиям (лабораторных показателей —
/// MedicalDocumentExtractionProcessor, и медикаментов по фото упаковки — MedicationOcrService) —
/// исправляет типичные артефакты распознавания: смешение похожих по начертанию кириллических и
/// латинских букв в одном слове ("СYMАТPИПTАН" → "Суматриптан"), случайный регистр (КАПС), лишние
/// пробелы — не трогая сам смысл названия. Выполняется ДО сопоставления со справочником
/// (LabAnalyteKbLookupService/KbLookupService, оба на pg_trgm) — грязное написание снижает
/// триграммную схожесть и порождает ложные промахи там, где для человека название очевидно.
///
/// Тот же приём "модель предлагает, детерминированный код ветирует", что и UserSpecimenService/
/// MedicationEnrichmentProcessor.ResolveCorrectedName: TrigramSimilarity ниже порога значит модель
/// подменила понятие целиком, а не поправила написание — коррекция отклоняется, остаётся исходное
/// имя. LM Studio недоступен ⇒ тоже исходные имена (в отличие от валидации биоматериала, тихий
/// пропуск здесь безопасен — хуже, чем без коррекции, не станет).
/// </summary>
public class OcrNameCorrector(ILmStudioJsonClient client, ILogger<OcrNameCorrector> logger)
{
    /// <summary>Тот же порог, что MedicationEnrichmentProcessor.ResolveCorrectedName/UserSpecimenService.</summary>
    private const double MinCorrectionSimilarity = 0.3;

    private const string SystemPrompt = """
        Ты — корректор текста, распознанного OCR/визуальным распознаванием на бланке медицинского
        документа или фото упаковки лекарства. На входе — пронумерованный список названий
        (показателей анализов или медикаментов), часть из которых могла быть распознана с ошибками:
        смешение похожих по начертанию русских и латинских букв в одном слове (например
        "СYMАТPИПTАН" вместо "Суматриптан", где кириллические С/А/Т/Н перемешаны с латинскими
        Y/M/P), случайный регистр (КАПС, чеРЕДование), лишние пробелы.

        Верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа: {"corrections": [{"index": 0, "corrected": "Суматриптан"}]}

        Правила:
        - Включай в "corrections" ТОЛЬКО те названия, которые ты реально исправил — если название
          уже написано нормально (один алфавит, обычный регистр), не включай его в ответ вовсе.
        - "corrected" — то же самое понятие, только с исправленным написанием: один алфавит на
          слово (не смешивай кириллицу и латиницу), обычный регистр (с заглавной буквы, остальные
          строчные, как в литературном тексте), без лишних пробелов.
        - НЕ переводи на другой язык, НЕ заменяй понятие другим похожим, НЕ добавляй ничего, чего
          не было в исходном названии — только исправление написания одного и того же слова/фразы.
        - Если не уверен, что название искажено распознаванием (может быть, это просто необычное
          название или сокращение) — не исправляй его, не включай в ответ.
        - "index" — номер из подписи "[N]" перед названием.
        - Верни строго один JSON-объект. Пустой массив "corrections": [], если исправлений нет.
        """;

    /// <summary>Один медикамент (MedicationOcrService — фото упаковки) — обёртка над CorrectBatchAsync.</summary>
    public async Task<string> CorrectAsync(string rawName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return rawName;
        var result = await CorrectBatchAsync([rawName], ct);
        return result.Count > 0 ? result[0] : rawName;
    }

    /// <summary>Батч на весь бланк/документ — один вызов локальной LLM вместо одного на показатель:
    /// она физически одна за WireGuard (см. LmStudioConcurrencyGate) — N отдельных вызовов означали
    /// бы N последовательных прогонов. Возвращает список той же длины и в том же порядке, что
    /// rawNames — непрошедшие проверку или неисправленные моделью имена возвращаются как есть.</summary>
    public async Task<IReadOnlyList<string>> CorrectBatchAsync(IReadOnlyList<string> rawNames, CancellationToken ct = default)
    {
        if (rawNames.Count == 0) return [];

        var distinct = rawNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0) return rawNames.ToList();

        var corrections = await RequestCorrectionsAsync(distinct, ct);
        return rawNames.Select(n => corrections.GetValueOrDefault(n, n)).ToList();
    }

    private async Task<Dictionary<string, string>> RequestCorrectionsAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        var corrected = new Dictionary<string, string>(StringComparer.Ordinal);

        var userText = BuildUserText(names);
        var result = await client.ExtractJsonAsync(SystemPrompt, userText, ct);
        if (result is null || !result.Success || result.Payload is null)
        {
            logger.LogInformation("Коррекция OCR-имён недоступна: {Error}", result?.Error);
            return corrected;
        }

        if (!TryGetValue(result.Payload, "corrections", out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
            return corrected;

        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var index = (int?)ReadDouble(item, "index");
            var candidate = ReadString(item, "corrected")?.Trim();
            if (index is null || index < 0 || index >= names.Count || string.IsNullOrEmpty(candidate)) continue;

            var original = names[index.Value];
            if (candidate == original) continue;

            // LabAnalyteNormalizer.Normalize (не просто ToLowerInvariant) — она уже чинит смешение
            // кириллицы/латиницы посимвольно (FixMixedScriptHomoglyphs), поэтому "СYMАТPИПTАН" и
            // "Суматриптан" здесь совпадают почти полностью, а не расходятся из-за разных code
            // points у визуально одинаковых букв. Строгое ToLowerInvariant без этого шага сравнивал
            // бы "сyмaтpиптaн" (латиница внутри) с "суматриптан" (кириллица) — низкая схожесть на
            // ровно том случае, который эта коррекция должна пропускать.
            var similarity = TrigramSimilarity.Similarity(
                LabAnalyteNormalizer.Normalize(original), LabAnalyteNormalizer.Normalize(candidate));
            if (similarity < MinCorrectionSimilarity)
            {
                logger.LogWarning(
                    "Коррекция OCR-имени: модель предложила «{Corrected}» вместо «{Original}», но схожесть " +
                    "{Similarity:F2} слишком низкая — похоже на другое понятие, отклонено.",
                    candidate, original, similarity);
                continue;
            }

            corrected[original] = candidate;
        }

        return corrected;
    }

    private static string BuildUserText(IReadOnlyList<string> names)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < names.Count; i++)
            sb.Append('[').Append(i).Append("] ").AppendLine(names[i]);
        return sb.ToString();
    }
}
