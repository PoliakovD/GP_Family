using FamilyHub.Infrastructure.Consents;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Extraction;
using FamilyHub.Modules.Medical.Kb;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FamilyHub.Modules.Medical.Ocr;
using FamilyHub.Modules.Medical.Pipeline;
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
        // Управление enrich-пайплайном из админки (§2) — вкл/выкл необязательных шагов.
        // PromptProvider/IPromptProvider — Infrastructure-уровня (см. class doc IPromptProvider:
        // используется и здесь, и поисковыми запросами Infrastructure.Enrichment), регистрируется
        // в Program.cs, не тут. IMemoryCache тоже регистрируется в Program.cs.
        services.AddScoped<PipelineConfigService>();
        services.AddScoped<IPipelineConfigService>(sp => sp.GetRequiredService<PipelineConfigService>());
        // Первый обязательный шаг каждого конвейера (см. PipelineCatalog.LegitimacyCheckStep) —
        // проверка легитимности/prompt injection до любого другого LLM-вызова этим прогоном.
        services.AddScoped<LegitimacyGuardService>();
        services.AddScoped<ILegitimacyGuardService>(sp => sp.GetRequiredService<LegitimacyGuardService>());

        services.AddScoped<MedkitService>();
        services.AddScoped<MedicationService>();
        services.AddScoped<MedicalRecordService>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<MedicationOcrService>();
        // Ветка medicalrecords (задачи 5.2/5.3): конвейер извлечения показателей анализов и
        // заключений врача. IMedicalDocumentExtractor регистрируется в Program.cs (переключатель
        // Extraction:Enabled, тот же паттерн, что Enrichment:Provider — не здесь, чтобы модуль не
        // решал, какая реализация активна) — по умолчанию Null, если хост её не переопределил.
        services.AddScoped<IMedicalDocumentExtractor, NullMedicalDocumentExtractor>();
        services.AddScoped<LabAnalyteKbLookupService>();
        services.AddScoped<PatientReferenceCalculator>();
        services.AddScoped<LabSummarizer>();
        services.AddScoped<ExtractionRequestService>();
        services.AddScoped<ExtractionQueryService>();
        services.AddScoped<MedicalDocumentExtractionProcessor>();
        // Обогащение справочника показателей (kb.global_lab_analytes_kb) — зеркало конвейера
        // медикаментов ниже, тот же IMedicationSearchProvider (Program.cs), другой topic
        // (WebSearchTopic.LabAnalyte).
        services.AddScoped<LabAnalyteKbWriter>();
        services.AddScoped<LabAnalyteKbSummarizer>();
        services.AddScoped<LabAnalyteSearchCacheService>();
        services.AddScoped<LabAnalyteEnrichmentRequestService>();
        services.AddScoped<LabAnalyteEnrichmentProcessor>();
        services.AddScoped<LabAnalyteKbReenrichJob>();
        services.AddScoped<LabAnalyteKbRebuildJob>();
        services.AddScoped<RecalculateIndicatorFlagsJob>();
        // Второй проход коррекции OCR (анализы + медикаменты, см. class doc) — общий на оба конвейера.
        services.AddScoped<OcrNameCorrector>();
        // Общий (не персональный) справочник источников показателя — биоматериал ИЛИ
        // инструментальное исследование, одна таблица на оба рода понятия (пересборка
        // enrich-пайплайна, никакого enum/switch в коде) — используется и ручным вводом
        // (UserSpecimenService), и резолвингом при извлечении документа (SpecimenResolver).
        services.AddScoped<GlobalSpecimenKbService>();
        services.AddScoped<SpecimenResolver>();
        services.AddScoped<UserSpecimenService>();
        // IRussianTextSearcher регистрируется в Program.cs (Infrastructure) — общий для этого
        // модуля и Modules.Birthdays, которые сознательно не ссылаются друг на друга.
        services.AddScoped<SearchService>();

        // Конвейер обогащения справочника (этап 4) — IMedicationSearchProvider регистрируется
        // в Program.cs (переключатель Enrichment:Provider, зеркало LmStudio/FileStorage выше).
        services.AddScoped<KbLookupService>();
        services.AddScoped<KbCatalogService>();
        // Справочник показателей (редизайн v2) — зеркало KbCatalogService выше на другую таблицу.
        services.AddScoped<KbAnalyteCatalogService>();
        services.AddScoped<MedicationKbStatusService>();
        services.AddScoped<KbWriter>();
        // Ручная правка справочников после ИИ из админки (§3 плана) — единственный писатель,
        // кроме автоматического обогащения (LabAnalyteKbWriter/KbWriter выше).
        services.AddScoped<AdminCatalogService>();
        services.AddScoped<MedicationSummarizer>();
        services.AddScoped<MedicationSearchCacheService>();
        // Доверенные домены обоих конвейеров — БД-backed, управляются через админку (см. class doc).
        services.AddScoped<EnrichmentTrustedDomainService>();
        services.AddScoped<IEnrichmentRequestService, EnrichmentRequestService>();
        services.AddScoped<MedicationEnrichmentProcessor>();
        // Обогащение справочника медикаментов из заключений врача (UX-редизайн) — отдельный
        // конвейер задач (не FamilyId-скоуп, см. VisitMedicationEnrichmentJob), тот же справочник.
        services.AddScoped<VisitMedicationEnrichmentRequestService>();
        services.AddScoped<VisitMedicationEnrichmentProcessor>();
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
        module.MapExtractionEndpoints();
        module.MapUserSpecimenEndpoints();
        module.MapSearchEndpoints();
        module.MapKbEndpoints();
    }
}
