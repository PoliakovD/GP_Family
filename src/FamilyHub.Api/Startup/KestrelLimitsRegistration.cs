using FamilyHub.Modules.Medical.Attachments;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — лимит размера тела запроса Kestrel, без
/// изменения поведения/порядка.
/// </summary>
public static class KestrelLimitsRegistration
{
    /// <summary>Явный запас над Attachments:MaxFileSizeBytes: без этого implicit-дефолт Kestrel
    /// (~28.6 МиБ, 30_000_000 байт) обрубал бы запрос СВОЕЙ, менее информативной ошибкой раньше,
    /// чем срабатывала бы наша проверка с понятным телом ответа ({code, maxSizeBytes}) — см. аудит
    /// module-review-2026-08-02/03-medical-records-attachments.md, находка 2. builder.Configuration
    /// уже наполнена на этом этапе (до app.Build()), поэтому читаем секцию напрямую, а не через
    /// IOptions — сервис-провайдер ещё не построен.</summary>
    public static WebApplicationBuilder AddFamilyHubKestrelLimits(this WebApplicationBuilder builder)
    {
        var attachmentUploadOptions = builder.Configuration.GetSection(AttachmentUploadOptions.SectionName).Get<AttachmentUploadOptions>()
            ?? new AttachmentUploadOptions();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Limits.MaxRequestBodySize = attachmentUploadOptions.MaxFileSizeBytes + 5 * 1024 * 1024);

        return builder;
    }
}
