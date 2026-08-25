using System.Text;
using Microsoft.Extensions.Logging;
using NPOI.SS.UserModel;
using NPOI.XWPF.Extractor;
using NPOI.XWPF.UserModel;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// Текст из офисных форматов через NPOI — .docx (XWPF), .xlsx/.xls (общий IWorkbook через
/// WorkbookFactory, детектирует формат по содержимому). Легаси .doc НЕ поддержан — см.
/// докстринг <see cref="DocumentContentTypes.Office"/>.
/// Таблицы Excel/Word намеренно превращаются в TSV-строки (табуляция между ячейками), а не в
/// сплошной текст: бланк анализа — это почти всегда таблица «показатель / значение / норма»,
/// и модели заметно легче разобрать её по столбцам, чем угадывать границы полей в потоке слов.
/// </summary>
public class OfficeDocumentReader(ILogger<OfficeDocumentReader> logger)
{
    public string? ExtractText(byte[] bytes, string contentType)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return contentType switch
            {
                DocumentContentTypes.Docx => ExtractDocx(stream),
                DocumentContentTypes.Xlsx or DocumentContentTypes.Xls => ExtractSpreadsheet(stream),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Не удалось прочитать офисный документ ({ContentType}) — вероятно, повреждён.", contentType);
            return null;
        }
    }

    private static string ExtractDocx(Stream stream)
    {
        using var document = new XWPFDocument(stream);
        var extractor = new XWPFWordExtractor(document);
        // XWPFWordExtractor.Text уже включает содержимое таблиц (см. README/исходники NPOI) —
        // отдельный обход document.Tables не нужен.
        return extractor.Text;
    }

    private static string ExtractSpreadsheet(Stream stream)
    {
        using var workbook = WorkbookFactory.Create(stream, readOnly: true);
        var sb = new StringBuilder();
        for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
        {
            var sheet = workbook.GetSheetAt(sheetIndex);
            for (var rowIndex = sheet.FirstRowNum; rowIndex <= sheet.LastRowNum; rowIndex++)
            {
                var row = sheet.GetRow(rowIndex);
                if (row is null) continue;

                var cells = new List<string>();
                for (var cellIndex = row.FirstCellNum; cellIndex < row.LastCellNum; cellIndex++)
                {
                    var cell = row.GetCell(cellIndex);
                    cells.Add(CellText(cell));
                }
                sb.AppendLine(string.Join('\t', cells));
            }
        }
        return sb.ToString();
    }

    private static string CellText(NPOI.SS.UserModel.ICell? cell)
    {
        if (cell is null) return string.Empty;
        return cell.CellType switch
        {
            CellType.Numeric => cell.NumericCellValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CellType.Formula => cell.ToString() ?? string.Empty,
            _ => cell.ToString() ?? string.Empty,
        };
    }
}
