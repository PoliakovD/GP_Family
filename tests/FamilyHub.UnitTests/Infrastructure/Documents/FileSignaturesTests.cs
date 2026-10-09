using System.Text;
using FamilyHub.Infrastructure.Documents;
using FluentAssertions;
using Xunit;

namespace FamilyHub.UnitTests.Infrastructure.Documents;

/// <summary>Сверка заявленного типа вложения с magic bytes (бэклог аудита security-audit-2026-10).</summary>
public class FileSignaturesTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>ISO BMFF: 4 байта размера бокса + "ftyp" + бренд.</summary>
    private static byte[] Ftyp(string brand) => [0, 0, 0, 0x18, .. Ascii("ftyp" + brand), 0, 0, 0, 0];

    public static TheoryData<string, byte[]> Valid => new()
    {
        { DocumentContentTypes.Pdf, Ascii("%PDF-1.7\n") },
        { DocumentContentTypes.Jpeg, [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10] },
        { DocumentContentTypes.Png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0] },
        { DocumentContentTypes.Webp, [.. Ascii("RIFF"), 0x24, 0, 0, 0, .. Ascii("WEBPVP8 ")] },
        { DocumentContentTypes.Tiff, [(byte)'I', (byte)'I', 42, 0, 8, 0] },
        { DocumentContentTypes.Tiff, [(byte)'M', (byte)'M', 0, 42, 0, 0] },
        { DocumentContentTypes.Heic, Ftyp("heic") },
        { DocumentContentTypes.Heic, Ftyp("mif1") },
        { DocumentContentTypes.Docx, [0x50, 0x4B, 0x03, 0x04, 0x14, 0] },
        { DocumentContentTypes.Xlsx, [0x50, 0x4B, 0x03, 0x04, 0x14, 0] },
        { DocumentContentTypes.Doc, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1] },
        { DocumentContentTypes.Xls, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1] },
        { DocumentContentTypes.Rtf, Ascii(@"{\rtf1\ansi") },
        { "APPLICATION/PDF", Ascii("%PDF-1.4") },
        // Текстовые форматы сигнатуры не имеют.
        { DocumentContentTypes.PlainText, Ascii("что угодно") },
        { DocumentContentTypes.Html, Ascii("<html></html>") },
        { DocumentContentTypes.Csv, [] },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void Matches_RealSignature_True(string contentType, byte[] head) =>
        FileSignatures.Matches(contentType, head).Should().BeTrue();

    public static TheoryData<string, byte[]> Invalid => new()
    {
        { DocumentContentTypes.Pdf, Ascii("<html><script>") },
        { DocumentContentTypes.Pdf, [] },
        { DocumentContentTypes.Jpeg, Ascii("%PDF-1.7") },
        { DocumentContentTypes.Png, [0xFF, 0xD8, 0xFF, 0xE0] },
        { DocumentContentTypes.Webp, [.. Ascii("RIFF"), 0x24, 0, 0, 0, .. Ascii("WAVEfmt ")] },
        { DocumentContentTypes.Tiff, Ascii("II*") },
        { DocumentContentTypes.Heic, Ftyp("isom") },
        { DocumentContentTypes.Docx, Ascii("not a zip") },
        { DocumentContentTypes.Doc, [0x50, 0x4B, 0x03, 0x04] },
        { DocumentContentTypes.Rtf, Ascii("rtf") },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Matches_WrongContent_False(string contentType, byte[] head) =>
        FileSignatures.Matches(contentType, head).Should().BeFalse();
}
