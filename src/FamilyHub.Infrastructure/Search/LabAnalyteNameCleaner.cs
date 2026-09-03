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
