using System.Text;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>Одно слово страницы с координатами в пространстве PDF (Y растёт вверх, Left&lt;Right,
/// Top&gt;Bottom) — тонкая проекция UglyToad.PdfPig.Content.Word.BoundingBox. Отдельный тип (не сам
/// Word из PdfPig) — чтобы LayoutTextReconstructor можно было юнит-тестировать синтетическими
/// координатами, без реального PDF-файла и без прямой зависимости тестов от PdfPig.</summary>
public readonly record struct PositionedWord(string Text, double Left, double Right, double Top, double Bottom)
{
    public double Height => Top - Bottom;
    public double CenterY => (Top + Bottom) / 2;
}

/// <summary>
/// Восстанавливает читаемый (визуальный) порядок текста страницы по координатам слов вместо
/// порядка операторов отрисовки в потоке содержимого PDF.
///
/// UglyToad.PdfPig.Content.Page.Text отдаёт буквы в ПОРЯДКЕ ИХ ОТРИСОВКИ в PDF — для табличных
/// бланков лабораторий это не порядок чтения: ячейки одной строки таблицы нередко напечатаны в
/// потоке в другом порядке, чем визуально. Живой пример (бланк Гемотест, распознавание анализов):
/// в потоке "Глюкоза 4.11-6.1 4.41 ммоль/л" — референсный диапазон "4.11-6.1" идёт ПЕРЕД значением
/// "4.41", хотя в таблице колонки идут "Результат | Ед.изм. | Реф.значения". Модель, читающая
/// текст в этом порядке, не может надёжно понять, какое число — результат, а какое — граница
/// нормы; это и есть одна из двух причин заниженного распознавания (см. план "качество
/// ИИ-распознавания анализов", Этап 1).
///
/// Слова уже правильно сегментированы Page.GetWords() (NearestNeighbourWordExtractor, склейки
/// букв без пробелов не даёт) — здесь только группировка в строки по Y и сортировка внутри строки
/// по X, плюс вставка "|" на больших горизонтальных разрывах (границы колонок таблицы), чтобы
/// дальнейший код (LabTableRowDetector) мог опираться на "ячейка | ячейка | ячейка", а не гадать
/// по одиночным пробелам, которые ничем не отличаются от пробела внутри многословного названия.
/// </summary>
public static class LayoutTextReconstructor
{
    /// <summary>Слово считается той же строкой, что и текущий кластер, если разница между его
    /// центром Y и средним центром Y кластера не превышает эту долю высоты слова — с запасом на
    /// надстрочные символы (стрелки ↑/↓ печатаются чуть выше базовой линии значения) и лёгкий
    /// наклон сканов, но заметно меньше типичного межстрочного интервала, чтобы не склеить две
    /// соседние строки таблицы.</summary>
    private const double LineToleranceFactor = 0.6;

    /// <summary>Горизонтальный разрыв между соседними словами строки больше этой доли высоты строки
    /// считается границей колонки таблицы, а не обычным пробелом внутри одной ячейки/фразы —
    /// обычный пробел между словами одной фразы в типографике заметно меньше высоты строки,
    /// разрыв между колонками таблицы — несколько высот.</summary>
    private const double ColumnGapFactor = 1.8;

    /// <summary>Нижняя граница порога колонки в pt — на мелком шрифте ColumnGapFactor*height может
    /// оказаться меньше ширины обычного пробела, что превратило бы каждый пробел в границу колонки.</summary>
    private const double MinColumnGapPt = 6.0;

    public static string Reconstruct(IReadOnlyList<PositionedWord> words)
    {
        if (words.Count == 0) return string.Empty;

        var lines = GroupIntoLines(words);
        return string.Join('\n', lines.Select(RenderLine));
    }

    private static List<List<PositionedWord>> GroupIntoLines(IReadOnlyList<PositionedWord> words)
    {
        // Сверху вниз (Y убывает в пространстве PDF при чтении сверху вниз), при равном Y — слева
        // направо, чтобы кластеризация по строкам была детерминированной.
        var ordered = words.OrderByDescending(w => w.CenterY).ThenBy(w => w.Left).ToList();

        var lines = new List<List<PositionedWord>>();
        var current = new List<PositionedWord> { ordered[0] };
        var currentAvgHeight = ordered[0].Height;
        var currentAvgY = ordered[0].CenterY;

        for (var i = 1; i < ordered.Count; i++)
        {
            var word = ordered[i];
            var tolerance = Math.Max(currentAvgHeight, word.Height) * LineToleranceFactor;
            if (Math.Abs(word.CenterY - currentAvgY) <= tolerance)
            {
                current.Add(word);
                currentAvgHeight = current.Average(w => w.Height);
                currentAvgY = current.Average(w => w.CenterY);
            }
            else
            {
                lines.Add(current);
                current = [word];
                currentAvgHeight = word.Height;
                currentAvgY = word.CenterY;
            }
        }
        lines.Add(current);
        return lines;
    }

    private static string RenderLine(List<PositionedWord> line)
    {
        var sorted = line.OrderBy(w => w.Left).ToList();
        var gapThreshold = Math.Max(Median(sorted.Select(w => w.Height)) * ColumnGapFactor, MinColumnGapPt);

        var sb = new StringBuilder(sorted[0].Text);
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i].Left - sorted[i - 1].Right;
            sb.Append(gap > gapThreshold ? " | " : " ");
            sb.Append(sorted[i].Text);
        }
        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }
}
