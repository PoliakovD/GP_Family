using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>Текстовый слой PDF (PdfPig) — путь для лабораторий, выгружающих готовый PDF, не
/// скан. Дёшево и точно по сравнению с vision-OCR каждой страницы (см. план: "текст напрямую,
/// vision — только для сканов").
///
/// Текст каждой страницы восстанавливается ПО КООРДИНАТАМ слов (LayoutTextReconstructor), не
/// через Page.Text — Page.Text отдаёт буквы в порядке операторов отрисовки PDF, для табличных
/// бланков лабораторий это не порядок чтения (см. докстринг LayoutTextReconstructor: значение и
/// референсный диапазон одной строки таблицы нередко идут в потоке в другом порядке, чем
/// визуально) и вдобавок иногда склеивает соседние слова без пробела. Page.GetWords() уже
/// правильно сегментирует слова (NearestNeighbourWordExtractor) — реконструктору остаётся только
/// расставить их по строкам/колонкам.</summary>
public class PdfDocumentReader(ILogger<PdfDocumentReader> logger)
{
    /// <summary>Ниже этого числа букв/цифр во всём документе текстовый слой считается
    /// отсутствующим (пустым или мусорным OCR-слоем сканера) — сигнал рендерить страницы как
    /// картинки вместо использования текста.</summary>
    private const int MinMeaningfulCharacters = 20;

    public PdfTextResult ExtractText(byte[] pdfBytes)
    {
        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            var sb = new StringBuilder();
            var pageNumber = 0;
            foreach (var page in document.GetPages())
            {
                pageNumber++;
                if (pageNumber > 1) sb.Append("\n\n--- стр. ").Append(pageNumber).Append(" ---\n\n");

                var words = page.GetWords()
                    .Select(w => new PositionedWord(w.Text, w.BoundingBox.Left, w.BoundingBox.Right, w.BoundingBox.Top, w.BoundingBox.Bottom))
                    .ToList();
                sb.Append(LayoutTextReconstructor.Reconstruct(words));
            }

            var text = sb.ToString();
            var meaningfulChars = text.Count(char.IsLetterOrDigit);
            return new PdfTextResult(true, text, HasTextLayer: meaningfulChars >= MinMeaningfulCharacters, document.NumberOfPages);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось открыть PDF через PdfPig — вероятно, повреждён или зашифрован.");
            return new PdfTextResult(false, null, false, 0);
        }
    }
}

public record PdfTextResult(bool Success, string? Text, bool HasTextLayer, int PageCount);
