using FamilyHub.Api.Features.Home;
using FamilyHub.Api.Features.Jobs;
using FamilyHub.Infrastructure.Documents;
using FamilyHub.Modules.Birthdays;
using FamilyHub.Modules.Medical;
using FamilyHub.Modules.Medical.Extraction;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — Medical/Birthdays-модули + кросс-модульные
/// агрегаты (живут в хосте, не в модуле, т.к. модули не ссылаются друг на друга напрямую), без
/// изменения поведения/порядка.
/// </summary>
public static class MedicalAndBirthdaysRegistration
{
    public static WebApplicationBuilder AddFamilyHubMedicalAndBirthdays(this WebApplicationBuilder builder)
    {
        // --- Medical-модуль ---
        builder.Services.AddMedicalModule();

        // --- Extraction: включение реального конвейера (ветка medicalrecords) — тот же паттерн
        // --- переключателя, что Enrichment:Provider выше. ПОСЛЕ AddMedicalModule(): ASP.NET Core DI
        // --- резолвит последнюю регистрацию для одиночного сервиса, эта строка обязана перекрыть
        // --- Null-регистрацию по умолчанию из MedicalModule, не наоборот. Без явного конфига — Null
        // --- (Extraction:Enabled по умолчанию true, но отсутствие LmStudio:Model/BaseUrl просто даст
        // --- Failed на каждой задаче, а не тишину — см. LmStudioHealthCheck).
        var extractionOptions = builder.Configuration.GetSection(ExtractionOptions.SectionName).Get<ExtractionOptions>()
            ?? new ExtractionOptions();
        if (extractionOptions.Enabled)
        {
            builder.Services.AddScoped<IMedicalDocumentExtractor, LmStudioMedicalDocumentExtractor>();
        }

        // --- Birthdays-модуль (этап 4 п.11) ---
        builder.Services.AddBirthdayModule();

        // --- Агрегат Главной (редизайн v2) — в хосте, не в модуле: собирает Medical+Birthdays,
        // --- которые не могут зависеть друг от друга напрямую (см. HomeSummaryService). ---
        builder.Services.AddScoped<HomeSummaryService>();

        // --- Глобальный индикатор фоновых процессов (§4 плана «живой конвейер») — та же причина, что у
        // --- HomeSummaryService: агрегирует все четыре таблицы задач Medical по текущему пользователю. ---
        builder.Services.AddScoped<UserJobsService>();

        return builder;
    }
}
