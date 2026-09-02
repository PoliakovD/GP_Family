using System.Text;
using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Общие детерминированные хелперы очистки текста, распознанного с бланков анализов (пересборка
/// enrich-пайплайна) — используются и ключом дедупликации (<see cref="LabAnalyteNormalizer"/>), и
/// отображаемым именем (<see cref="LabAnalyteNameCleaner"/>): починка гомоглифов и снятие
/// служебных префиксов нумерации — общая механика, разный результат (ключ vs. текст для
/// человека), поэтому вынесено сюда, а не продублировано в обоих местах.
/// </summary>
internal static partial class LabTextCleanupHelpers
{
    /// <summary>Латинские буквы, визуально неотличимые от кириллических — артефакт OCR.</summary>
    private static readonly Dictionary<char, char> LatinToCyrillicHomoglyphs = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['E'] = 'Е', ['K'] = 'К', ['M'] = 'М', ['H'] = 'Н',
        ['O'] = 'О', ['P'] = 'Р', ['C'] = 'С', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
        ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
    };

    /// <summary>Эхо-индекс, которым OcrNameCorrector/LabAnalyteKbSummarizer подписывают элементы
    /// списка перед отправкой модели ("[0] Гемоглобин") — модель иногда возвращает его обратно
    /// вместе с исправленным текстом вместо того, чтобы вернуть только исправление.</summary>
    [GeneratedRegex(@"^\s*\[\d{1,4}\]\s*")]
    private static partial Regex EchoIndexRegex();

    /// <summary>Номер пункта бланка перед названием: "1. ", "12) ", "1.2 ", голое "5 " (обычно —
    /// след схлопнутой в одну строку таблицы бланка, см. PdfDocumentReader). Не трогает составные
    /// обозначения без пробела после цифр ("17-ОН-прогестерон", "25-OH витамин D") — там после
    /// цифр сразу дефис, а не разделитель пункта/пробел.</summary>
    [GeneratedRegex(@"^\s*\d{1,3}(?:[.)]\d{1,3})*[.)]?\s+")]
    private static partial Regex NumberedItemRegex();

    /// <summary>Снимает эхо-индекс и нумерацию пункта бланка с начала строки — используется и
    /// ключом (до нормализации регистра), и отображаемым именем (без изменения регистра).</summary>
    public static string StripLeadingMarkers(string input)
    {
        var withoutEcho = EchoIndexRegex().Replace(input, string.Empty);
        return NumberedItemRegex().Replace(withoutEcho, string.Empty);
    }

    /// <summary>Правит смешение кириллицы/латиницы внутри одного слова ("Гемoглoбин" →
    /// "Гемоглобин") — не трогает чисто латинские слова (аббревиатуры, англоязычные названия).</summary>
    public static string FixMixedScriptHomoglyphs(string input)
    {
        var words = input.Split(' ');
        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w];
            if (!HasCyrillic(word) || !HasLatin(word)) continue;

            var sb = new StringBuilder(word.Length);
            foreach (var ch in word)
                sb.Append(LatinToCyrillicHomoglyphs.TryGetValue(ch, out var mapped) ? mapped : ch);

            words[w] = sb.ToString();
        }

        return string.Join(' ', words);
    }

    public static bool HasCyrillic(string s) => s.Any(ch => ch is >= 'Ѐ' and <= 'ӿ');

    public static bool HasLatin(string s) => s.Any(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
}
