using System.Text;
using System.Text.RegularExpressions;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.LmStudio;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Prompts;
using Microsoft.Extensions.Logging;
using static FamilyHub.Infrastructure.LmStudio.LmStudioPayloadReader;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Сырой (ещё не применённый к именам показателей) итог одного LLM-прохода по документу —
/// см. AnalyteSubjectResolver.ResolveAsync. <see cref="Subject"/> заполнен, только когда весь
/// документ посвящён ОДНОМУ конкретному объекту исследования (посев на определённый
/// микроорганизм, один аллерген, одно антитело/ген), а таблица показателей при этом печатает лишь
/// родовое слово ("Бактериальные микроорганизмы") — обычный анализ с многими показателями (ОАК,
/// биохимия) сюда не попадает, там Subject всегда null.</summary>
public record AnalyteSubjectResolution(string? Subject, string? RawLabel, string? Evidence, double Confidence)
{
    public static readonly AnalyteSubjectResolution Empty = new(null, null, null, 0);
}

/// <summary>
/// Резолвинг ОБЪЕКТА исследования для бланков, где таблица показателей называет его только
/// родовым словом, а конкретика печатается отдельно — обычно в разделе "Оказанные услуги" внизу
/// документа (пересборка после наблюдения: посев кала на сальмонеллы и посев кала на
/// стафилококк — два разных файла одной записи — приходят с ОДИНАКОВОЙ строкой "Бактериальные
/// микроорганизмы — не выявлено" в таблице; без уточнения оба показателя схлопываются в один по
/// ключу дедупликации, см. MedicalDocumentExtractionProcessor).
///
/// Тот же приём "модель предлагает, детерминированный код ветирует", что уже есть в
/// SpecimenResolver/OcrNameCorrector — здесь ДОПОЛНИТЕЛЬНО проверяется, что предложенный субъект
/// реально встречается в тексте документа (аналог антигаллюцинационного гейта
/// LmStudioMedicalDocumentExtractor, которому этот резолвер не подчиняется напрямую — он вызывается
/// уже ПОСЛЕ структурирования показателей, отдельным проходом, а не побочным полем того промпта:
/// совмещение задач в одном вызове мешало бы обеим, тот же довод, что в SpecimenResolver).
///
/// Оба детерминированных вето — ЧЕРЕЗ <see cref="IRussianTextSearcher"/> (стемминг + AND по
/// словам), не через <see cref="TrigramSimilarity"/> строки целиком, как у SpecimenResolver/
/// OcrNameCorrector: там обе стороны сравнения — короткие однословные понятия ("кровь" vs "кровь",
/// "Ибупрофен" vs "Парацетамол"), здесь же "subject" — короткое название (1-3 слова), а "rawLabel"/
/// текст документа — предложение или весь документ целиком. Триграммное сходство ЦЕЛОЙ строки
/// в этом случае тонет в шуме длинного текста, даже когда субъект в нём дословно есть, И не
/// переживает обычную русскую морфологию (бланк пишет «рода сальмонелла», модель называет субъект
/// «Сальмонеллы» — разные окончания, но то же слово) — оба случая ловились этим гейтом как
/// «модель придумала», хотя ответ был верным (см. живой пример, план "5 файлов посева").
///
/// Третий случай той же природы, продовое наблюдение: subject и rawLabel/текст документа написаны
/// РАЗНЫМИ АЛФАВИТАМИ ("Аденовирусы" против "Antigen Adenovirus") — Score стеммингом кириллицу с
/// латиницей не сравнивает вовсе (разные code points), релевантность 0.00 при очевидном для
/// человека совпадении. Обе стороны обоих вето сворачиваются через
/// <see cref="MedicalTextTransliterator.Fold"/> ПЕРЕД вызовом Score — см. ResolveAsync.
/// </summary>
public partial class AnalyteSubjectResolver(
    ILmStudioJsonClient client, IRussianTextSearcher searcher, IPromptProvider promptProvider,
    ILogger<AnalyteSubjectResolver> logger)
{
    /// <summary>Таксономический "хвост"-квалификатор с коротким обозначением группы/типа за ним:
    /// "gr.A", "тип 41", "serotype 2" — модель предлагает subject С хвостом ("Rotavirus gr.A"),
    /// а в тексте документа встречается только базовое название ("Rotavirus") без него. Это не
    /// алфавитный барьер (см. class doc MedicalTextTransliterator) — AND-семантика
    /// IRussianTextSearcher.Score рубит совпадение на токенах "gr"/"a", которых в rawLabel/тексте
    /// попросту нет. Ведущий \s+ обязателен — без него "Группа крови" (квалификатор в НАЧАЛЕ
    /// строки, не хвост) обрезалась бы неверно. "Гепатит B"/"Гепатит C" не задевает — там после
    /// буквы нет слова-квалификатора, обозначение вирусного гепатита значимо само по себе.</summary>
    [GeneratedRegex(
        @"\s+\b(?:gr|grp|group|гр|группа|spp|sp|subsp|var|тип|type|serotype|серотип)\b\.?\s*[a-zа-я0-9]{1,3}\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TaxonomicQualifierWithDesignatorRegex();

    /// <summary>Тот же хвост, но без короткого обозначения после него ("Salmonella spp.",
    /// "Rotavirus var.") — квалификатор сам по себе ничего не уточняет, снимается целиком.</summary>
    [GeneratedRegex(@"\s+\b(?:spp|sp|subsp|var)\b\.?(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BareTaxonomicQualifierRegex();

    /// <summary>Снимает таксономический хвост с subject ПЕРЕД сравнением (см. доки регулярок выше) —
    /// не применяется к rawLabel/тексту документа, там хвост, если он есть, — часть цитаты бланка.</summary>
    private static string StripTaxonomicQualifiers(string subject)
    {
        var withoutDesignated = TaxonomicQualifierWithDesignatorRegex().Replace(subject, string.Empty);
        return BareTaxonomicQualifierRegex().Replace(withoutDesignated, string.Empty);
    }

    /// <summary>Тот же порог, что SpecimenResolver.MinConfidence — ниже него субъект считается
    /// нерезолвленным.</summary>
    public const double MinConfidence = 0.7;

    /// <summary>Тот же дефолт, что pg_trgm.similarity_threshold (см. RussianTextSearcher) —
    /// предложенный "subject" обязан реально встречаться (морфологически, не дословно) и в
    /// "rawLabel", и в тексте документа.</summary>
    private const double MinRelevance = 0.3;

    /// <summary>Хвост документа читается ПОЛНОСТЬЮ (в отличие от SpecimenResolver.HeaderChars) —
    /// "Оказанные услуги"/"Наименование исследования" печатаются внизу бланка, шапки одной
    /// недостаточно. Ограничение всё же нужно — не гнать в модель мегабайтный документ целиком.</summary>
    private const int MaxChars = 4000;

    private const string SystemPrompt = """
        Ты — уточнитель объекта лабораторного исследования. На входе — текст (или фото) бланка
        анализа и список показателей, которые уже извлечены из его таблицы результатов.

        Некоторые виды анализов (посев на конкретный микроорганизм, аллергопроба на конкретный
        аллерген, анализ на конкретное антитело или ген) печатают в самой ТАБЛИЦЕ результатов
        только РОДОВОЕ слово ("Бактериальные микроорганизмы", "Аллерген", "Антитела класса IgG") —
        а то, что искали КОНКРЕТНО, названо только в другом месте документа, обычно в разделе
        "Оказанные услуги", "Наименование исследования", "Назначенные исследования" или в названии
        самого направления/заказа. Твоя задача — найти это конкретное название, только если оно
        РЕАЛЬНО ЕСТЬ в документе где-то за пределами таблицы. Верни ТОЛЬКО валидный JSON, без
        пояснений, без markdown, без блока <think>.

        Формат ответа:
        {
          "subject": "конкретный объект исследования литературным названием (например, \"Сальмонеллы\", \"Клещ домашней пыли\") или null",
          "rawLabel": "как объект назван в документе — цитата или близкий пересказ, или null",
          "evidence": "короткая цитата из документа, где это написано, или null",
          "confidence": 0.0
        }

        Правила:
        - Заполняй "subject", ТОЛЬКО если весь документ посвящён ОДНОМУ конкретному объекту
          исследования — а показатели в таблице названы лишь родовым/обобщённым словом. Если
          документ — обычный многострочный анализ (общий анализ крови, биохимия, общий анализ
          мочи), где у каждого показателя УЖЕ есть своё конкретное название — "subject": null,
          "confidence": 0, даже если где-то в документе упоминаются отдельные вещества/клетки.
        - Не путай родовое слово таблицы ("Бактериальные микроорганизмы", "Аллерген", "Антитела")
          с конкретным названием самого исследования ("Посев на Salmonella spp.", "Определение
          IgE к клещу домашней пыли") — именно второе и есть "subject", первое — то, что нужно
          уточнить.
        - Если конкретное название есть только в общих словах ("бактериологическое исследование"
          без указания микроорганизма) — этого недостаточно, "subject": null.
        - "confidence" — число от 0 до 1, твоя уверенность именно в том, что это ОДИН конкретный
          объект и что ты нашёл его верное название. Не придумывай — если не уверен, ставь низкое
          значение (менее 0.5).
        - Верни строго один JSON-объект, ничего кроме него.
        """;

    public async Task<AnalyteSubjectResolution> ResolveAsync(
        DocumentContent content, IReadOnlyList<string> indicatorNames, CancellationToken ct = default)
    {
        var prompt = await promptProvider.GetAsync("analysis.subject-resolve", SystemPrompt, ct);
        LmStudioJsonResult result;

        if (content.Kind == DocumentSourceKind.Text && !string.IsNullOrEmpty(content.Text))
        {
            var userText = BuildUserText(content.Text, indicatorNames);
            result = await client.ExtractJsonAsync(prompt, userText, ct);
        }
        else if (content.Kind == DocumentSourceKind.Image && content.Images.Count > 0)
        {
            var userText = BuildUserText(null, indicatorNames);
            result = await client.ExtractJsonAsync(
                prompt, userText, content.Images.Select(i => (i.Bytes, i.ContentType)).ToList(), ct);
        }
        else
        {
            return AnalyteSubjectResolution.Empty;
        }

        if (!result.Success || result.Payload is null)
        {
            logger.LogInformation("Резолвинг объекта исследования недоступен: {Error}", result.Error);
            return AnalyteSubjectResolution.Empty;
        }

        var subject = ReadString(result.Payload, "subject");
        var rawLabel = ReadString(result.Payload, "rawLabel");
        var confidence = ReadDouble(result.Payload, "confidence") ?? 0;

        if (string.IsNullOrWhiteSpace(subject) || confidence < MinConfidence) return AnalyteSubjectResolution.Empty;

        // Обе стороны обоих вето ниже сравниваются в СВЁРНУТОЙ форме — не в исходной. Живое продовое
        // наблюдение: subject "Аденовирусы" для rawLabel "Антиген Adenovirus (B,C,E)" давал
        // релевантность 0.00 — не потому что модель ошиблась, а потому что "adenovirus"/"аденовирус"
        // разными code points не пересекаются по стеммингу/триграммам вовсе (алфавитный барьер, см.
        // MedicalTextTransliterator). StripTaxonomicQualifiers снимает отдельную, не алфавитную
        // причину того же провала: subject "Rotavirus gr.A" рубится AND-семантикой Score на
        // токенах "gr"/"a", которых в rawLabel просто нет — словарём терминов это не лечится, там
        // нечего переводить, "группы" в исходной строке нет вовсе.
        var comparableSubject = MedicalTextTransliterator.Fold(StripTaxonomicQualifiers(subject));

        // Детерминированное вето — та же форма, что SpecimenResolver.ResolveKbIdAsync/
        // OcrNameCorrector.RequestCorrectionsAsync: предложенное название не должно оказаться
        // другим понятием, чем то, что реально написано в документе. IRussianTextSearcher.Score —
        // по словам с русской морфологией (AND-семантика), не по строке целиком: "rawLabel"
        // обычно целая фраза услуги ("...на микроорганизмы рода сальмонелла (Salmonella spp.)"),
        // а не короткое понятие вроде specimen — целостное триграммное сравнение тонуло бы в её
        // длине, даже когда субъект в ней дословно есть.
        if (!string.IsNullOrWhiteSpace(rawLabel))
        {
            var relevance = searcher.Score(MedicalTextTransliterator.Fold(rawLabel), comparableSubject);
            if (relevance < MinRelevance)
            {
                logger.LogWarning(
                    "Резолвинг объекта исследования: модель предложила «{Subject}» для «{RawLabel}», но релевантность " +
                    "{Relevance:F2} слишком низкая — отклонено.", subject, rawLabel, relevance);
                return AnalyteSubjectResolution.Empty;
            }
        }

        // Антигаллюцинационный гейт: субъект обязан реально встречаться в тексте документа — иначе
        // модель его придумала. Тот же IRussianTextSearcher, не дословный Contains — бланк почти
        // всегда склоняет название ("рода сальмонелла"), а модель называет субъект литературно
        // ("Сальмонеллы") — разные окончания одного слова, дословное совпадение после Normalize
        // здесь систематически не срабатывает. На vision-пути (нет исходного текста) проверка
        // недоступна по построению, тот же компромисс, что у LmStudioMedicalDocumentExtractor.
        if (content.Kind == DocumentSourceKind.Text && !string.IsNullOrEmpty(content.Text))
        {
            var relevance = searcher.Score(MedicalTextTransliterator.Fold(content.Text), comparableSubject);
            if (relevance < MinRelevance)
            {
                logger.LogWarning(
                    "Резолвинг объекта исследования: модель предложила «{Subject}», но это не встречается в " +
                    "тексте документа (релевантность {Relevance:F2}) — отклонено.", subject, relevance);
                return AnalyteSubjectResolution.Empty;
            }
        }

        return new AnalyteSubjectResolution(subject.Trim(), rawLabel, ReadString(result.Payload, "evidence"), confidence);
    }

    /// <summary>Хвост документа читается целиком (не только шапка, см. class doc) — "Оказанные
    /// услуги" печатаются внизу бланка. MaxChars — тот же компромисс, что ChunkOverlapChars/
    /// MaxCharsPerChunk у LmStudioMedicalDocumentExtractor: длинный документ всё равно режется, но
    /// здесь важнее хвост, чем начало, поэтому при превышении лимита сохраняется именно ХВОСТ, а
    /// не голова.</summary>
    private static string BuildUserText(string? documentText, IReadOnlyList<string> indicatorNames)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(documentText))
        {
            var tail = documentText.Length > MaxChars ? documentText[^MaxChars..] : documentText;
            sb.AppendLine("Текст документа:").AppendLine(tail).AppendLine();
        }
        else
        {
            sb.AppendLine("Определи объект исследования на этом изображении.").AppendLine();
        }

        sb.AppendLine("Показатели, извлечённые из таблицы результатов:");
        foreach (var name in indicatorNames) sb.Append("- ").AppendLine(name);
        return sb.ToString();
    }
}
