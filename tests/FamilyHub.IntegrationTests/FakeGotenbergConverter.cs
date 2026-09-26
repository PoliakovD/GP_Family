using System.Text;
using FamilyHub.Infrastructure.Previews;

namespace FamilyHub.IntegrationTests;

/// <summary>
/// Заглушка сайдкара Gotenberg для интеграционных тестов: HTML→PDF отдаёт фиксированный минимальный
/// PDF (настоящий Chromium в тестовом стеке не поднимается), а конвертация офисных документов —
/// по-прежнему null, как у NullGotenbergConverter, чтобы не менять поведение тестов вложений.
/// </summary>
public sealed class FakeGotenbergConverter : IGotenbergConverter
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>endobj\ntrailer<</Root 1 0 R/Size 4>>\n%%EOF\n");

    public Task<byte[]?> ConvertToPdfAsync(byte[] content, string contentType, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(null);

    public Task<byte[]?> ConvertHtmlToPdfAsync(string html, string? footerHtml = null, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(Pdf);
}
