using System.Text;
using System.Text.Json;
using FamilyHub.Infrastructure.Enrichment;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Pipeline;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Суммаризация веб-сниппетов доверенных источников локальным Qwen (этап 4, шаг 4 конвейера).
/// Сниппеты на входе уже отфильтрованы процессором обогащения по доверенным доменам (пересборка
/// enrich-пайплайна — см. EnrichmentSnippetFilter/EnrichmentTrustedDomainService в
/// MedicationEnrichmentProcessor; сам провайдер и кэш больше не фильтруют) — поэтому достаточно
/// проверить, что модель вообще на что-то сослалась (антигаллюцинационный гейт), не перепроверять
/// домен каждого индекса здесь же. Также может предложить исправленное
/// название препарата (MedicationSummary.CorrectedName) — OCR по фото упаковки иногда искажает
/// название ("Сумматрептан" вместо "Суматриптан"), и без исправления неверное имя навсегда
/// оседало бы как DisplayName/NormalizedName записи справочника (см. MedicationEnrichmentProcessor,
/// где коррекция дополнительно проверяется на схожесть с исходным именем).
/// </summary>
public class MedicationSummarizer(ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<MedicationSummarizer> logger)
{
    private const string SystemPrompt = """
        Ты — медицинский справочный ассистент. На входе — название препарата и пронумерованные
        сниппеты со страниц проверенных медицинских справочников РФ. Проанализируй ТОЛЬКО
        переданные сниппеты (ничего не добавляй от себя, никаких знаний "из головы") и верни ТОЛЬКО
        валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "internationalName": "международное непатентованное название (МНН) или null",
          "tradeNames": ["торговое название", "..."],
          "form": "форма выпуска (таблетки/капли/сироп и т.д.) или null",
          "purpose": "назначение/показания к применению или null",
          "simplePurpose": "то же назначение, но простыми бытовыми словами без медицинских терминов, понятно человеку без медицинского образования (например, не \"жаропонижающее\", а \"сбивает температуру\"; не \"антигистаминное\", а \"от аллергии\") или null",
          "usage": "способ применения и дозы — КАК НАПИСАНО В ИНСТРУКЦИИ (общие данные для препарата, не для конкретного человека) или null",
          "storage": "условия хранения или null",
          "driving": "влияние на способность управлять транспортом или null",
          "specialNotes": "противопоказания, побочные эффекты, меры предосторожности и другие важные рекомендации из инструкции или null",
          "correctedName": "настоящее название препарата, если переданное название искажено (опечатка, ошибка распознавания по фото упаковки), а сниппеты явно указывают на конкретный другой препарат — иначе null",
          "usedSourceIndexes": [0, 2]
        }

        Правила:
        - "simplePurpose" — коротко и просто, обычными словами, которыми говорят в быту, а не
          медицинскими терминами. Если по сути совпадает с "purpose" и упростить нечего — можно
          оставить null.
        - Извлекай МАКСИМУМ полезной информации из сниппетов. Способ применения, дозы,
          противопоказания и побочные эффекты — обычные разделы инструкции к препарату, не
          медицинская консультация: указывай их так, как они есть в источнике, не сокращай и не
          пропускай специально.
        - "correctedName" заполняй ТОЛЬКО когда уверен, что переданное название — искажённая
          форма ОДНОГО конкретного препарата из сниппетов (например, "Сумматрептан" →
          "Суматриптан" — явная опечатка того же слова). Если сниппеты про несколько разных
          вероятных препаратов или ты не уверен — оставь null, не гадай.
        - "usedSourceIndexes" — индексы (из подписи "[N]" перед каждым сниппетом) источников,
          на которые реально опирается ответ. Если ни один сниппет не содержит полезной
          информации о препарате — верни пустой массив и null во всех текстовых полях.
        - "tradeNames" — пустой массив, если по сниппетам не удалось определить ни одного названия.
        - Каждое текстовое поле — по существу, без искусственного сокращения и без дословного
          копирования целого сниппета.
        - Верни строго один JSON-объект, ничего кроме него.
        """;
    public async Task<SummarizeResult> SummarizeAsync(
        string displayName, IReadOnlyList<WebSnippet> snippets, CancellationToken ct = default)
    {
        if (snippets.Count == 0)
        {
            return SummarizeResult.Failure("Нет сниппетов от доверенных источников — суммаризировать нечего.");
        }

        var userText = BuildUserText(displayName, snippets);
        var prompt = await promptProvider.GetAsync("medication.summarize", SystemPrompt, ct);
        var result = await client.ExtractJsonAsync(prompt, userText, ct);
        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Суммаризация «{DisplayName}» не удалась: {Error}", displayName, result.Error);
            return SummarizeResult.Failure(result.Error ?? "Модель не вернула структурированный ответ.");
        }

        var usedIndexes = ReadIndexArray(result.Payload, "usedSourceIndexes")
            .Where(i => i >= 0 && i < snippets.Count)
            .Distinct()
            .ToList();

        // Антигаллюцинационный гейт: модель обязана сослаться хотя бы на один сниппет доверенного
        // источника (все они уже прошли фильтр по домену на процессоре, см. class doc) — иначе в
        // справочник не пишем вовсе, не только текст без ссылок (см. task-5.1-medications.md).
        if (usedIndexes.Count == 0)
        {
            logger.LogInformation(
                "Суммаризация «{DisplayName}»: модель не сослалась ни на один источник — запись в справочник отклонена.",
                displayName);
            return SummarizeResult.Failure("Модель не смогла подтвердить ответ ни одним источником.");
        }

        // Clean, не Normalize — торговые названия/исправленное имя идут дальше как отображаемый
        // текст (KbWriter.Aliases/DisplayName), не как ключ сравнения. Снимает случайный мусор
        // вроде эхо-нумерации, если модель скопировала кусок сниппета буквально вместе со
        // служебной разметкой — тот же приём, что LabAnalyteKbSummarizer применяет к
        // aliases/relatedAnalytes (пересборка enrich-пайплайна, §5 плана).
        var tradeNames = ReadStringArray(result.Payload, "tradeNames")
            .Select(LabAnalyteNameCleaner.Clean).Where(n => n.Length > 0).Distinct().ToList();
        var correctedName = ReadString(result.Payload, "correctedName");
        var cleanedCorrectedName = string.IsNullOrWhiteSpace(correctedName) ? null : LabAnalyteNameCleaner.Clean(correctedName);

        var summary = new MedicationSummary(
            InternationalName: ReadString(result.Payload, "internationalName"),
            TradeNames: tradeNames,
            Form: ReadString(result.Payload, "form"),
            Purpose: ReadString(result.Payload, "purpose"),
            SimplePurpose: ReadString(result.Payload, "simplePurpose"),
            Usage: ReadString(result.Payload, "usage"),
            Storage: ReadString(result.Payload, "storage"),
            Driving: ReadString(result.Payload, "driving"),
            SpecialNotes: ReadString(result.Payload, "specialNotes"),
            UsedSourceIndexes: usedIndexes,
            CorrectedName: cleanedCorrectedName);

        // Второе условие гейта: индексы есть, но контента по сути нет (модель сослалась на
        // источник, где не нашла ничего полезного) — тоже не пишем пустую строку в справочник.
        var hasContent = summary.TradeNames.Count > 0 || new[]
        {
            summary.InternationalName, summary.Form, summary.Purpose, summary.SimplePurpose, summary.Usage,
            summary.Storage, summary.Driving, summary.SpecialNotes,
        }.Any(f => !string.IsNullOrWhiteSpace(f));

        if (!hasContent)
        {
            logger.LogInformation("Суммаризация «{DisplayName}»: все поля пусты — запись в справочник отклонена.", displayName);
            return SummarizeResult.Failure("Модель не извлекла ни одного содержательного поля.");
        }

        return SummarizeResult.Ok(summary);
    }

    private static string BuildUserText(string displayName, IReadOnlyList<WebSnippet> snippets)
    {
        var sb = new StringBuilder();
        sb.Append("Препарат: ").AppendLine(displayName).AppendLine();
        for (var i = 0; i < snippets.Count; i++)
        {
            var s = snippets[i];
            sb.Append('[').Append(i).Append("] ").AppendLine(s.Title);
            sb.AppendLine(s.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? ReadString(Dictionary<string, JsonElement> payload, string key) =>
        TryGetValue(payload, key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static List<string> ReadStringArray(Dictionary<string, JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el) || el.ValueKind != JsonValueKind.Array) return [];

        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static List<int> ReadIndexArray(Dictionary<string, JsonElement> payload, string key)
    {
        if (!TryGetValue(payload, key, out var el) || el.ValueKind != JsonValueKind.Array) return [];

        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
            .Select(e => e.GetInt32())
            .ToList();
    }

    /// <summary>Регистронезависимый поиск ключа — как в MedicationOcrService, модель иногда меняет регистр.</summary>
    private static bool TryGetValue(Dictionary<string, JsonElement> payload, string key, out JsonElement value)
    {
        foreach (var (k, v) in payload)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = default;
        return false;
    }
}
