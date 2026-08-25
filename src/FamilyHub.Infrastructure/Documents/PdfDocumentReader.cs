using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>Текстовый слой PDF (PdfPig) — путь для лабораторий, выгружающих готовый PDF, не
/// скан. Дёшево и точно по сравнению с vision-OCR каждой страницы (см. план: "текст напрямую,
/// vision — только для сканов").</summary>
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
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
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
