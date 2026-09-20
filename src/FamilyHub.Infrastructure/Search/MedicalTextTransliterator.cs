using System.Text;
using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Search;

/// <summary>
/// Свёртка кириллицы и латиницы в одну сравнимую форму — устраняет провал алфавитного барьера у
/// детерминированных вето extraction-пайплайна (<c>OcrNameCorrector</c>/<c>AnalyteSubjectResolver</c>
/// в Modules.Medical): OCR/модель нередко пишут название показателя то по-русски, то оригинальной
/// латиницей ("Antigen Adenovirus" / "Антиген аденовирус"), и обе стороны сравнения — одно и то же
/// понятие, но <see cref="TrigramSimilarity"/>/<see cref="IRussianTextSearcher"/> считают их
/// непохожими: символьные триграммы латинского "adenovirus" и кириллического "аденовирус" почти не
/// пересекаются (разные code points), хотя произносятся одинаково. Продовое наблюдение: схожесть
/// 0.27/0.12 на парах, которые для человека — одно и то же слово, см. план "кросс-алфавитное
/// сопоставление названий показателей".
///
/// <see cref="Fold"/> используется в ДВУХ ролях. (1) Транзитно — подставляется по обе стороны
/// сравнения в момент расчёта схожести для детерминированных вето
/// (OcrNameCorrector/AnalyteSubjectResolver), ничего не меняя в том, что уходит в БД напрямую.
/// (2) Как финальный шаг <see cref="LabAnalyteNormalizer.NormalizeAnalyteKey"/> — ПОСТОЯННЫЙ ключ
/// дедупликации <c>LabIndicator.AnalyteKey</c>/<c>GlobalLabAnalyteKb.NormalizedName</c> (миграция
/// существующих строк — <c>LabAnalyteKbRebuildJob</c>, план "миграция AnalyteKey"; НЕ путать с
/// <see cref="LabAnalyteNormalizer.Normalize"/> без свёртки — тот остаётся ключом для
/// specimen-объектов, см. doc NormalizeAnalyteKey). Одна и та же функция на обе роли намеренно —
/// расхождение между "чем вето меряет схожесть" и "что реально лежит в ключе" было бы источником
/// нового варианта ровно того же бага, который эта функция и чинит.
///
/// Три механики, в порядке применения к каждому слову:
/// 1. <see cref="LabTextCleanupHelpers.FixMixedScriptHomoglyphs"/> — уже существующая починка
///    смешанных кириллица+латиница слов ("Гемoглoбин" → "Гемоглобин"), переиспользуется как есть.
/// 2. Однобуквенный латинский токен — ВИЗУАЛЬНЫЙ гомоглиф ("Гепатит B" ↔ "Гепатит В": латинское B
///    внутри бланка почти всегда означает кириллическую В на слух, а не звук "б" — фонетическая
///    транслитерация здесь дала бы неверный "б" и разорвала бы как раз тот случай, который нужно
///    починить).
/// 3. Многобуквенное латинское слово — словарь целых терминов (когда транслитерация была бы
///    бессмысленной, "surface" → "сурфаце" вместо "поверхность"), а при промахе — фонетическая
///    транслитерация ("adenovirus" → "аденовирус", "rotavirus" → "ротавирус").
///
/// После этого — <see cref="FoldCyrillicWord"/> (снять ь/ъ, й→и, схлопнуть двойные буквы) над КАЖДЫМ
/// словом независимо от происхождения: без этого "salmonella"→"салмонелла" (двойное "л" из
/// исходного "ll") не сходится с нативным "Сальмонелла" (мягкий знак вместо второго "л" по правилам
/// русской транслитерации) — обе формы схлопываются к одной "салмонела".
///
/// Стемминга в <see cref="Fold"/> НЕТ (ни здесь, ни в <see cref="IsTranslationOf"/>, см. её doc) —
/// нечёткое триграммное сравнение и так переживает разницу падежей на несколько символов; там, где
/// нужна именно морфология (AND по словам), её снимает <see cref="IRussianTextSearcher.Score"/>
/// (AnalyteSubjectResolver) — свой стеммер, отдельно от Fold.
/// </summary>
public static partial class MedicalTextTransliterator
{
    /// <summary>Буквенные последовательности — цифры и пунктуация словом не считаются, остаются на
    /// месте (не мешают <see cref="RussianTextSearcher"/> ретокенизировать результат заново).</summary>
    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordRegex();

    /// <summary>Однобуквенный латинский токен — визуально то же самое, что кириллическая буква на
    /// бланке (обозначение подтипа "Гепатит B/C", группы крови "AB0"), а не отдельное слово для
    /// транслитерации. Отличается от фонетической таблицы ниже: там 'b'→'б', здесь — 'b'→'в'.</summary>
    private static readonly Dictionary<char, char> VisualSingleLetterHomoglyphs = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['d'] = 'д', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к',
        ['m'] = 'м', ['o'] = 'о', ['p'] = 'р', ['t'] = 'т', ['x'] = 'х', ['y'] = 'у',
    };

    /// <summary>Диграфы латиницы, читаемые НЕ как сумма фонетики отдельных букв — проверяются перед
    /// одиночными буквами (порядок ниже — от длинных к коротким, взаимно исключающие пары на одной
    /// позиции не пересекаются).</summary>
    private static readonly (string Latin, string Cyrillic)[] PhoneticDigraphs =
    [
        ("sch", "ш"),
        ("zh", "ж"), ("kh", "х"), ("ch", "ч"), ("sh", "ш"), ("ph", "ф"), ("th", "т"),
        ("ts", "ц"), ("ck", "к"), ("qu", "кв"), ("ya", "я"), ("yu", "ю"), ("ee", "и"), ("oo", "у"),
    ];

    /// <summary>Фонетическая транслитерация одиночной буквы вне диграфа. 'c' сюда не входит —
    /// правило контекстное (перед e/i/y читается как 'с', иначе как 'к'), см. <see cref="TransliterateWord"/>.</summary>
    private static readonly Dictionary<char, char> PhoneticSingleLetters = new()
    {
        ['a'] = 'а', ['b'] = 'б', ['d'] = 'д', ['e'] = 'е', ['f'] = 'ф', ['g'] = 'г', ['h'] = 'х',
        ['i'] = 'и', ['j'] = 'й', ['k'] = 'к', ['l'] = 'л', ['m'] = 'м', ['n'] = 'н', ['o'] = 'о',
        ['p'] = 'п', ['q'] = 'к', ['r'] = 'р', ['s'] = 'с', ['t'] = 'т', ['u'] = 'у', ['v'] = 'в',
        ['w'] = 'в', ['y'] = 'и', ['z'] = 'з',
        // 'x' — не одиночная буква, а пара звуков "кс": обрабатывается отдельной веткой.
    };

    /// <summary>Английские медицинские термины, чья транслитерация была бы бессмысленной или неверной
    /// (например, 'h' в "hepatitis" читался бы как 'х', хотя в русском заимствовании — "гепатИТ" с
    /// 'г') — целыми словами, не буквами. Ключи в нижнем регистре.</summary>
    private static readonly Dictionary<string, string> TermDictionary = new(StringComparer.Ordinal)
    {
        ["surface"] = "поверхность",
        ["antibody"] = "антитело",
        ["antibodies"] = "антитело",
        ["hepatitis"] = "гепатит",
        ["blood"] = "кровь",
        ["urine"] = "моча",
        ["stool"] = "кал",
        ["feces"] = "кал",
        ["faeces"] = "кал",
        ["serum"] = "сыворотка",
        ["total"] = "общий",
        ["free"] = "свободный",
        ["direct"] = "прямой",
        ["detection"] = "обнаружение",
        ["count"] = "количество",
        ["cells"] = "клетки",
        ["cell"] = "клетка",
        ["cholesterol"] = "холестерин",
        ["protein"] = "белок",
        ["influenza"] = "грипп",
        ["measles"] = "корь",
        ["rubella"] = "краснуха",
        ["tuberculosis"] = "туберкулез",
        ["thyroid"] = "щитовидный",
    };

    /// <summary>Сравнимая форма строки: латиница свёрнута в кириллицу (гомоглифы/словарь/
    /// транслитерация), обе стороны затем прогнаны через <see cref="FoldCyrillicWord"/> — см. class
    /// doc. Пустая/пробельная строка → <see cref="string.Empty"/>.</summary>
    public static string Fold(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var fixedScript = LabTextCleanupHelpers.FixMixedScriptHomoglyphs(raw);
        var lower = fixedScript.ToLowerInvariant().Replace('ё', 'е');

        return WordRegex().Replace(lower, m => FoldCyrillicWord(TransformWord(m.Value)));
    }

    /// <summary>Порог схожести СВЁРНУТЫХ (не стеммированных) форм, при котором кандидат считается
    /// переводом оригинала, а не другим понятием — заметно выше <c>MinCorrectionSimilarity</c>
    /// (0.3) вызывающего кода: перевод отличается от оригинала лишь падежными окончаниями
    /// нескольких слов ("гепатита вируса" вместо "гепатит вирус" — 2-3 буквы на десятки символов
    /// строки), а не структурой в целом. На двух прод-примерах даёт 1.00 и 0.76 против 0.00 у
    /// пары разных понятий — разрыв большой, 0.5 берёт середину с запасом в обе стороны.</summary>
    private const double TranslationSimilarityThreshold = 0.5;

    /// <summary>
    /// true, если <paramref name="candidate"/> — перевод/транслитерация <paramref name="original"/>
    /// на другой алфавит (то же понятие), а не правка написания. Используется как "не ругаться, но и
    /// не применять" — см. OcrNameCorrector: перевод недетерминирован (один и тот же бланк модель
    /// в другой раз прочтёт иначе) и не должен уезжать в DisplayName/RawDisplayName.
    ///
    /// Намеренно БЕЗ стемминга (в отличие от более ранней версии этого метода) — RussianStemmer
    /// ложно принимает медицинские заимствования на "-ит" за глагольное окончание 3-го лица
    /// ("гепатит" → "гепат", как "любит" → "люб"), из-за чего "гепатит" и "гепатита" получали
    /// РАЗНЫЕ основы и настоящий перевод не распознавался. Триграммная схожесть свёрнутых форм
    /// целиком устойчивее к этой конкретной лексике и не зависит от корректности стемминга.
    ///
    /// Сигнал "оригинал написан на другом языке" — НЕ простой <c>HasLatin(original)</c> на сырой
    /// строке (принимать ЛЮБУЮ латинскую букву в оригинале за "это другой язык" — регрессия:
    /// "SYMATPIPTAH", OCR-перемешавший алфавит ВНУТРИ одного кириллического слова, тоже прошёл бы
    /// эту проверку и попал бы под вето, хотя это ровно тот случай, который OcrNameCorrector обязан
    /// пропускать как правку написания). И не на полностью свёрнутой <see cref="Fold"/>-форме —
    /// та транслитерирует ЛЮБОЕ латинское слово, включая настоящие иностранные "Adenovirus"/
    /// "Hepatitis", стирая сигнал ровно там, где он нужнее. Верный сигнал —
    /// <see cref="HasUntranslatedLatinWord"/>: латинское СЛОВО длиной больше одной буквы остаётся
    /// после починки ТОЛЬКО внутрисловного смешения (FixMixedScriptHomoglyphs, без полной свёртки).
    /// Однобуквенные токены (designator вроде "B" в "Гепатит B"/"(B,C,E)") не считаются словом на
    /// другом языке — та же граница, что у визуального гомоглифа в TransformWord.
    /// </summary>
    public static bool IsTranslationOf(string? original, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(candidate)) return false;
        if (!HasUntranslatedLatinWord(original) || HasUntranslatedLatinWord(candidate)) return false;

        return TrigramSimilarity.Similarity(Fold(original), Fold(candidate)) >= TranslationSimilarityThreshold;
    }

    private static bool HasUntranslatedLatinWord(string raw)
    {
        var scriptFixed = LabTextCleanupHelpers.FixMixedScriptHomoglyphs(raw);
        foreach (Match m in WordRegex().Matches(scriptFixed))
            if (m.Value.Length > 1 && LabTextCleanupHelpers.HasLatin(m.Value))
                return true;
        return false;
    }

    private static string TransformWord(string word)
    {
        if (!LabTextCleanupHelpers.HasLatin(word)) return word;

        if (word.Length == 1)
        {
            return VisualSingleLetterHomoglyphs.TryGetValue(word[0], out var visual)
                ? visual.ToString()
                : TransliterateWord(word);
        }

        return TermDictionary.TryGetValue(word, out var term) ? term : TransliterateWord(word);
    }

    private static string TransliterateWord(string word)
    {
        var sb = new StringBuilder(word.Length);
        var i = 0;
        while (i < word.Length)
        {
            var matchedDigraph = false;
            foreach (var (latin, cyrillic) in PhoneticDigraphs)
            {
                if (i + latin.Length > word.Length) continue;
                if (!word.AsSpan(i, latin.Length).SequenceEqual(latin)) continue;

                sb.Append(cyrillic);
                i += latin.Length;
                matchedDigraph = true;
                break;
            }

            if (matchedDigraph) continue;

            var ch = word[i];
            if (ch == 'c')
            {
                var next = i + 1 < word.Length ? word[i + 1] : '\0';
                sb.Append(next is 'e' or 'i' or 'y' ? 'с' : 'к');
            }
            else if (ch == 'x')
            {
                sb.Append("кс");
            }
            else if (PhoneticSingleLetters.TryGetValue(ch, out var mapped))
            {
                sb.Append(mapped);
            }
            else
            {
                sb.Append(ch); // неизвестная буква (не латиница/расширенный алфавит) — как есть
            }

            i++;
        }

        return sb.ToString();
    }

    /// <summary>Снимает ь/ъ, схлопывает й→и, схлопывает подряд идущие повторы буквы — приводит
    /// нативную кириллицу и результат транслитерации к одной форме независимо от того, как каждая
    /// сторона решила передать удвоение/мягкость согласной ("Salmonella"→"салмонелла" против нативного
    /// "Сальмонелла" — обе схлопываются в "салмонела", см. class doc).</summary>
    private static string FoldCyrillicWord(string word)
    {
        var sb = new StringBuilder(word.Length);
        char? prev = null;

        foreach (var ch in word)
        {
            char? mapped = ch switch
            {
                'ь' or 'ъ' => null,
                'й' => 'и',
                _ => ch,
            };

            if (mapped is null) continue;
            if (mapped == prev) continue;

            sb.Append(mapped.Value);
            prev = mapped.Value;
        }

        return sb.ToString();
    }
}
