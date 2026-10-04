using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Приводит распознанное название показателя анализа к виду ДЛЯ ОТОБРАЖЕНИЯ пользователю —
/// не путать с <see cref="LabAnalyteNormalizer.Normalize"/>, который даёт ключ дедупликации:
/// "1. ГЕМОГЛОБИН (HGB), г/л" → Clean → "Гемоглобин (HGB), г/л" (для человека), тот же вход через
/// Normalize → "гемоглобин" (ключ). Разные задачи: ключ агрессивно режет всё, что мешает
/// сравнению (скобки, единицы, регистр целиком); Clean только чинит артефакты распознавания
/// (нумерацию пункта, эхо-индекс, гомоглифы, случайный КАПС), не трогая смысл — единицы измерения
/// и сокращение в скобках пользователь должен продолжать видеть как есть.
/// </summary>
public static partial class LabAnalyteNameCleaner
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[,:\-]+\s*$")]
    private static partial Regex TrailingPunctuationRegex();

    /// <summary>Ниже этой длины токен не понижается регистром, даже если строка целиком КАПС —
    /// типичная длина медицинских сокращений ("СОЭ", "АЛТ", "ЛПНП", "Hb").</summary>
    private const int MinLowercaseableTokenLength = 5;

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var withoutMarkers = LabTextCleanupHelpers.StripLeadingMarkers(raw.Trim());
        var fixedScript = LabTextCleanupHelpers.FixMixedScriptHomoglyphs(withoutMarkers);
        var casedProperly = NormalizeCasing(fixedScript);
        var trimmedPunctuation = TrailingPunctuationRegex().Replace(casedProperly, string.Empty);
        return WhitespaceRegex().Replace(trimmedPunctuation, " ").Trim();
    }

    /// <summary>Модель (промпт просит "литературный регистр") и справочник нередко приводят
    /// аббревиатуры к обычному виду: "СРБ" → "Срб", "АЧТВ" → "Ачтв". Если candidate — то же имя,
    /// что и напечатанное в бланке (source), с точностью до регистра, а в бланке есть слово-
    /// аббревиатура (2+ буквы, все заглавные), возвращается написание из бланка; иначе candidate
    /// без изменений. Целиком-КАПС бланк дальше приводит в порядок Clean (короткие токены
    /// сохраняются, длинные слова понижаются).</summary>
    public static string RestoreAbbreviations(string candidate, string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate)) return candidate;
        var src = source.Trim();
        if (!string.Equals(candidate.Trim(), src, StringComparison.OrdinalIgnoreCase)) return candidate;
        var hasAbbreviation = WhitespaceRegex().Split(src)
            .SelectMany(w => w.Split('-', '(', ')', ',', '/'))
            .Any(t => t.Count(char.IsLetter) >= 2 && t.Any(char.IsLetter) && !t.Any(char.IsLower));
        return hasAbbreviation ? src : candidate;
    }

    /// <summary>Маркеры отклонения, которые бланк печатает рядом со значением: «*», «↑»/«↓», «!», «H»/«L».</summary>
    private const string FlagMarkers = @"(?:\s*(?:[*↑↓!]|\b[HL]\b))*";

    /// <summary>Хвост имени из бланка, который — одно лишь число (значение) с маркерами отклонения: «29,8», «29.8 ↓».</summary>
    [GeneratedRegex(@"^\s*[<>≤≥]?\s*\d+(?:[.,]\d+)?" + FlagMarkers + @"\s*$")]
    private static partial Regex NumericTailRegex();

    /// <summary>Название показателя из ячейки бланка БЕЗ слипшегося с ним значения: в узкой колонке PDF «название» и
    /// «результат» нередко склеиваются в одну ячейку («MCV (ср. объем эритр.) 84.2»). Значение (и маркеры отклонения
    /// «*», «↑»/«↓», «H»/«L») отрезается с конца, только если ячейка им действительно заканчивается. Десятичный
    /// разделитель не важен: модель нормализует «29,8» бланка в «29.8», и строгое сравнение оставляло значение в имени
    /// («Гематокрит крови 29,8» — дальше это имя уходило в ключ, мимо справочника, и в текст платного поиска).</summary>
    public static string BlankNameWithoutValue(string? cell, string? value)
    {
        var name = (cell ?? string.Empty).Trim();
        var v = (value ?? string.Empty).Trim().TrimEnd('*').Trim();
        if (v.Length == 0) return name;

        // Приклеенное значение отделено от названия пробелом ("… эритр.) 84.2"); без пробела это часть названия ("Витамин B12").
        var valuePattern = string.Join("[.,]", v.Split('.', ',').Select(Regex.Escape));
        var match = Regex.Match(name, $@"\s{valuePattern}{FlagMarkers}\s*$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var stripped = name[..match.Index].TrimEnd();
            if (stripped.Length > 0) return stripped;
        }

        return name;
    }

    /// <summary>Модель нередко сокращает название («MCV (ср. объем эритр.)» → «MCV», «Эозинофилы, %» → «Эозинофилы»), а
    /// пользователь открывает анализ, чтобы прочитать название из бланка целиком. Если имя из бланка (после той же
    /// чистки) НАЧИНАЕТСЯ с имени модели и длиннее него — берётся полное имя из бланка; иначе — имя модели без изменений
    /// (переименование/перевод/исправление опечатки модели не перетираем).</summary>
    public static string PreferFullBlankName(string candidate, string? blankName, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(blankName)) return candidate;

        var cleanedBlank = Clean(blankName);
        var cleanedCandidate = Clean(candidate);
        if (cleanedCandidate.Length == 0 || cleanedBlank.Length <= cleanedCandidate.Length || cleanedBlank.Length > maxLength) return candidate;
        if (!cleanedBlank.StartsWith(cleanedCandidate, StringComparison.OrdinalIgnoreCase)) return candidate;

        // Граница слова: «Гемоглобин» → «Гемоглобин (HGB)», но не «Гем» → «Гемоглобин».
        var next = cleanedBlank[cleanedCandidate.Length];
        if (next is not (' ' or '(' or ',' or '/' or '-')) return candidate;

        // Ячейка со слипшимся значением, которое BlankNameWithoutValue не отрезал («Гематокрит крови 29,8»): «полное»
        // имя отличается лишь числом — это не название, оставляем имя модели.
        return NumericTailRegex().IsMatch(cleanedBlank[cleanedCandidate.Length..]) ? candidate : cleanedBlank;
    }

    /// <summary>Та же чистка (нумерация/эхо-индекс/гомоглифы), но КАПС разбирается по словам, а не
    /// по фразе целиком — для ФИО ("ИВАНОВ ИВАН ИВАНОВИЧ" → "Иванов Иван Иванович"), где каждое
    /// слово — отдельное имя собственное, а не одна многословная фраза вроде "Общий белок"
    /// (пересборка enrich-пайплайна, §5 плана — врач/пациент в записи). <see cref="NormalizeCasing"/>
    /// капитализирует только первый токен фразы целиком и намеренно не трогает короткие/латинские/
    /// с цифрой токены (сокращения вроде "СОЭ") — для ФИО оба допущения неверны: короткое имя
    /// ("Иван", "Ян") — не сокращение, и капитализировать нужно КАЖДОЕ слово, не только первое.</summary>
    public static string CleanPersonName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var withoutMarkers = LabTextCleanupHelpers.StripLeadingMarkers(raw.Trim());
        var fixedScript = LabTextCleanupHelpers.FixMixedScriptHomoglyphs(withoutMarkers);

        var cased = IsAllCaps(fixedScript)
            ? string.Join(' ', fixedScript.Split(' ').Select(w => LowercaseWithLeadingUpper(w, capitalize: true)))
            : fixedScript;

        var trimmedPunctuation = TrailingPunctuationRegex().Replace(cased, string.Empty);
        return WhitespaceRegex().Replace(trimmedPunctuation, " ").Trim();
    }

    /// <summary>КАПС ("ГЕМОГЛОБИН (HGB), Г/Л") приводится к обычному регистру с заглавной первой
    /// буквой; регистр, в котором уже есть строчные буквы, не трогается вовсе — детерминированный
    /// код не пытается угадывать смысл случайного чеРЕДования регистра (это остаётся необязательному
    /// LLM-шагу OcrNameCorrector, если он включён), только однозначный случай сплошного КАПС.
    /// Аббревиатуры/коды не понижаются: латиница ("HGB", "IgG"), токены с цифрой ("B12", "17-ОН",
    /// "Т4") и короткие (&lt; <see cref="MinLowercaseableTokenLength"/> символов, "СОЭ", "АЛТ",
    /// "ЛПНП") остаются как есть.</summary>
    private static string NormalizeCasing(string input)
    {
        if (!IsAllCaps(input)) return input;

        // Решение принимается по КУСКУ, не по слову целиком: дефисные составы ("17-ОН-прогестерон")
        // разбираются по дефису, и каждый кусок судится отдельно — "17"/"ОН" остаются как есть
        // (код/сокращение), а "прогестерон" всё равно приводится к обычному регистру. Не жертвуем
        // читаемостью настоящего слова ради соседства с кодом в одном составном токене.
        var words = input.Split(' ');
        for (var w = 0; w < words.Length; w++)
        {
            var pieces = words[w].Split('-');
            for (var p = 0; p < pieces.Length; p++)
            {
                var piece = pieces[p];
                if (piece.Length == 0 || ShouldPreserveCasing(piece)) continue;

                pieces[p] = LowercaseWithLeadingUpper(piece, capitalize: w == 0 && p == 0);
            }
            words[w] = string.Join('-', pieces);
        }

        return string.Join(' ', words);
    }

    /// <summary>Строка целиком КАПС — есть хотя бы одна буква и нет ни одной строчной (цифры и
    /// пунктуация не в счёт).</summary>
    private static bool IsAllCaps(string input) => input.Any(char.IsLetter) && !input.Any(char.IsLower);

    private static bool ShouldPreserveCasing(string piece) =>
        LabTextCleanupHelpers.HasLatin(piece) || piece.Any(char.IsDigit) || piece.Length < MinLowercaseableTokenLength;

    private static string LowercaseWithLeadingUpper(string piece, bool capitalize)
    {
        var lower = piece.ToLowerInvariant();
        return capitalize && lower.Length > 0 ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
    }
}
