using FamilyHub.Infrastructure.Consents;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FamilyHub.Modules.Medical.Ocr;
using FamilyHub.Modules.Medical.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.Modules.Medical;

/// <summary>
/// Точка входа модуля «Аптечка + Анализы» (раздел 3 брифа — модуль зависит только от
/// Domain/Infrastructure, не от других модулей).
/// </summary>
public static class MedicalModule
{
    public static IServiceCollection AddMedicalModule(this IServiceCollection services)
    {
        services.AddScoped<MedkitService>();
        services.AddScoped<MedicationService>();
        services.AddScoped<MedicalRecordService>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<MedicationOcrService>();
        // Заготовка под OCR анализов/заключений (задачи 5.2/5.3 — не реализованы). По умолчанию
        // Null: ни очереди, ни эндпоинта, вызывающего этот сервис, ещё нет.
        services.AddScoped<IMedicalDocumentExtractor, NullMedicalDocumentExtractor>();
        // IRussianTextSearcher регистрируется в Program.cs (Infrastructure) — общий для этого
        // модуля и Modules.Birthdays, которые сознательно не ссылаются друг на друга.
        services.AddScoped<SearchService>();

        // Конвейер обогащения справочника (этап 4) — IMedicationSearchProvider регистрируется
        // в Program.cs (переключатель Enrichment:Provider, зеркало LmStudio/FileStorage выше).
        services.AddScoped<KbLookupService>();
        services.AddScoped<KbCatalogService>();
        services.AddScoped<MedicationKbStatusService>();
        services.AddScoped<KbWriter>();
        services.AddScoped<MedicationSummarizer>();
        services.AddScoped<MedicationSearchCacheService>();
        services.AddScoped<IEnrichmentRequestService, EnrichmentRequestService>();
        services.AddScoped<MedicationEnrichmentProcessor>();
        return services;
    }

    public static void MapMedicalModule(this IEndpointRouteBuilder app)
    {
        // Обработка медданных доступна только принявшим актуальное согласие ПДн (задача 2.3):
        // обёртка-группа навешивает ConsentRequiredFilter на все эндпоинты модуля.
        var module = app.MapGroup("").AddEndpointFilter<ConsentRequiredFilter>();
        module.MapMedkitEndpoints();
        module.MapMedicationEndpoints();
        module.MapMedicalRecordEndpoints();
        module.MapAttachmentEndpoints();
        module.MapMedicationOcrEndpoints();
        module.MapSearchEndpoints();
        module.MapKbEndpoints();
    }
}
