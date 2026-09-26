using Microsoft.Extensions.Logging;

namespace FamilyHub.Infrastructure.Previews;

/// <summary>Регистрируется, когда Previews:GotenbergBaseUrl пуст (сайдкар не поднят в этом
/// окружении) — тот же приём, что NullMedicationSearchProvider: fail-soft, не fail-fast, потому
/// что предпросмотр Office-документов не является требованием для работы приложения в целом.</summary>
public class NullGotenbergConverter(ILogger<NullGotenbergConverter> logger) : IGotenbergConverter
{
    public Task<byte[]?> ConvertToPdfAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        logger.LogDebug(
            "Previews:GotenbergBaseUrl не задан — конвертация {ContentType} в PDF пропущена, файл останется без предпросмотра.",
            contentType);
        return Task.FromResult<byte[]?>(null);
    }

    public Task<byte[]?> ConvertHtmlToPdfAsync(string html, string? footerHtml = null, CancellationToken ct = default)
    {
        logger.LogDebug("Previews:GotenbergBaseUrl не задан — HTML→PDF недоступен.");
        return Task.FromResult<byte[]?>(null);
    }
}
