using FamilyHub.Infrastructure.Documents;
using FamilyHub.Infrastructure.Previews;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — декодирование вложений под конвейер
/// извлечения (ветка medicalrecords) + генерация превью (Анализы/Врачи), без изменения
/// поведения/порядка. Documents и Previews объединены в один файл — оба маленькие и оба про один
/// и тот же конвейер обработки вложений.
/// </summary>
public static class AttachmentPipelineRegistration
{
    public static WebApplicationBuilder AddFamilyHubAttachmentPipeline(this WebApplicationBuilder builder)
    {
        // --- Документы: декодирование вложений под конвейер извлечения (ветка medicalrecords) ---
        builder.Services.AddScoped<PdfDocumentReader>();
        builder.Services.AddScoped<OfficeDocumentReader>();
        builder.Services.AddScoped<IDocumentTextExtractor, DocumentTextExtractor>();

        // --- Превью вложений (Анализы/Врачи): растровый путь переиспользует конвейер OCR выше
        // --- (PdfPageRasterizer/ImageDownscaler), Office идёт через сайдкар Gotenberg (LibreOffice) —
        // --- у NPOI (OfficeDocumentReader) нет движка вёрстки, только извлечение текста. Пусто
        // --- Previews:GotenbergBaseUrl — Null-реализация (fail-soft: Office-документы деградируют в
        // --- карточку «Скачать», как и любой Failed/Unsupported предпросмотр, см. AttachmentPreviewRenderer).
        builder.Services.AddScoped<AttachmentPreviewRenderer>();
        var previewsOptions = builder.Configuration.GetSection(PreviewOptions.SectionName).Get<PreviewOptions>() ?? new PreviewOptions();
        if (previewsOptions.Enabled && !string.IsNullOrWhiteSpace(previewsOptions.GotenbergBaseUrl))
        {
            builder.Services.AddHttpClient<IGotenbergConverter, GotenbergConverter>((sp, client) =>
            {
                var previews = sp.GetRequiredService<IOptions<PreviewOptions>>().Value;
                client.BaseAddress = new Uri(previews.GotenbergBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(previews.GotenbergTimeoutSeconds);
            });
        }
        else
        {
            builder.Services.AddSingleton<IGotenbergConverter, NullGotenbergConverter>();
        }

        return builder;
    }
}
