using System.Text;
using System.Text.RegularExpressions;

namespace FamilyHub.Infrastructure.Documents;

/// <summary>
/// txt/csv напрямую, html/rtf — грубой очисткой разметки (не полноценный парсер: этого формата
/// от медицинских выгрузок почти не встречается, лёгкая очистка тегов/control words достаточна,
/// чтобы модель увидела текст, а не мусор разметки). Кодировка — BOM → UTF-8 → cp1251, т.к.
/// многие старые выгрузки РФ-лабораторий в csv/txt приходят в Windows-1251 без BOM.
/// </summary>
public static partial class PlainTextReader
{
    static PlainTextReader()
    {
        // Windows-1251 не входит в базовый рантайм .NET Core/Linux — регистрация провайдера
        // нужна один раз за жизнь процесса, здесь безопасно вызывать многократно (идемпотентно).
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[] bytes, string contentType)
    {
        var raw = DecodeText(bytes);
        return contentType switch
        {
            DocumentContentTypes.Html => StripHtml(raw),
            DocumentContentTypes.Rtf => StripRtf(raw),
            _ => raw,
        };
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // UTF8Encoding со strict-декодером (throwOnInvalidBytes: true) — единственный надёжный
        // способ ОТЛИЧИТЬ настоящий UTF-8 от cp1251, которая тоже "успешно" декодируется как
        // UTF-8 заменяющими символами без исключения при обычном Encoding.UTF8.GetString.
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var cp1251 = Encoding.GetEncoding(1251);
            return cp1251.GetString(bytes);
        }
    }

    private static string StripHtml(string html)
    {
        var noScripts = ScriptOrStyleRegex().Replace(html, " ");
        var noTags = TagRegex().Replace(noScripts, "\n");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return CollapseBlankLinesRegex().Replace(decoded, "\n").Trim();
    }

    private static string StripRtf(string rtf)
    {
        var noGroups = RtfControlWordRegex().Replace(rtf, string.Empty);
        var noBraces = RtfBraceRegex().Replace(noGroups, string.Empty);
        return CollapseBlankLinesRegex().Replace(noBraces, "\n").Trim();
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\\[a-z]+-?\d* ?")]
    private static partial Regex RtfControlWordRegex();

    [GeneratedRegex(@"[{}]")]
    private static partial Regex RtfBraceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex CollapseBlankLinesRegex();
}
