using UglyToad.PdfPig;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>Число страниц готового PDF — управляемый PdfPig, без нативных зависимостей (в отличие от
/// PDFium в <see cref="PdfPageRasterizer"/>, который нужен только для растра).</summary>
public static class PdfPageCounter
{
    /// <summary>0 — байты не разобрались как PDF (не бросаем: счётчик страниц — справочная метрика).</summary>
    public static int Count(byte[] pdfBytes)
    {
        try
        {
            using var document = PdfDocument.Open(pdfBytes);
            return document.NumberOfPages;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
