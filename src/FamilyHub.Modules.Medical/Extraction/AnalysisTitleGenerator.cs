using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Короткое название анализа (заметка 4) — отдельный LLM-проход по шапке документа И уже
/// извлечённым показателям, а не побочное поле промпта структурирования (AnalysisSystemPrompt
/// раньше просило "suggestedTitle" в том же ответе, что и разбор таблицы показателей —
/// совмещение задач мешало обеим, ровно та же причина, по которой раньше выделили
/// SpecimenResolver). Видя РЕАЛЬНЫЙ состав панели (не только шапку, которая может быть пустой или
/// нечитаемой), модель даёт заметно более точное название, чем прежнее поле. Опционален
/// (§2 плана, PipelineCatalog) — выключен из админки означает, что название остаётся null (не
/// правится вручную здесь — это отдельный существующий путь через "Редактировать" на записи).
/// </summary>
public class AnalysisTitleGenerator(
    ILmStudioJsonClient client, IPromptProvider promptProvider, ILogger<AnalysisTitleGenerator> logger)
{
    /// <summary>Шапка бланка — название анализа, если оно вообще напечатано, почти всегда в первых
    /// строках; то же ограничение, что у SpecimenResolver.HeaderChars.</summary>
    private const int HeaderChars = 2000;

    private const string SystemPrompt = """
        Ты — специалист по коротким названиям медицинских анализов. На входе — шапка документа
        (может быть неполной или отсутствовать) и список показателей, которые реально удалось
        извлечь из этого бланка. Придумай КОРОТКОЕ (2-5 слов) литературное название анализа — то,
        как его обычно называют в направлении врача или в быту (например, "Общий анализ крови",
        "Биохимический анализ крови", "Общий анализ мочи", "Спермограмма", "Гормоны щитовидной
        железы"). Верни ТОЛЬКО валидный JSON, без пояснений, без markdown, без блока <think>.

        Формат ответа: {"title": "Общий анализ крови"}

        Правила:
        - Если название анализа напечатано в шапке документа прямо — используй его в литературном
          виде (без номера бланка/названия лаборатории/лишних слов).
        - Если в шапке названия нет — определи его по составу показателей (например, гемоглобин +
          эритроциты + лейкоциты → "Общий анализ крови").
        - Если по показателям тоже невозможно понять, что это за анализ — верни {"title": null},
          не придумывай название наугад.
        - Название должно быть коротким и узнаваемым — не пересказывай список показателей и не
          перечисляй их через запятую вместо названия.
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<string?> GenerateAsync(
        DocumentContent content, IReadOnlyList<string> indicatorNames, CancellationToken ct = default)
    {
        if (indicatorNames.Count == 0) return null;

        var prompt = await promptProvider.GetAsync("analysis.title", SystemPrompt, ct);
        var userText = BuildUserText(content, indicatorNames);

        var result = content.Kind == DocumentSourceKind.Image && content.Images.Count > 0
            ? await client.ExtractJsonAsync(prompt, userText, [(content.Images[0].Bytes, content.Images[0].ContentType)], ct)
            : await client.ExtractJsonAsync(prompt, userText, ct);

        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Генерация названия анализа недоступна: {Error}", result.Error);
            return null;
        }

        var title = ReadString(result.Payload, "title")?.Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    private static string BuildUserText(DocumentContent content, IReadOnlyList<string> indicatorNames)
    {
        var indicatorsLine = "Показатели: " + string.Join(", ", indicatorNames);
        if (content.Kind != DocumentSourceKind.Text || string.IsNullOrEmpty(content.Text)) return indicatorsLine;

        var header = content.Text.Length > HeaderChars ? content.Text[..HeaderChars] : content.Text;
        return $"{header}\n\n{indicatorsLine}";
    }
}
