using FamilyHub.Infrastructure.Consents;
using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
using FamilyHub.Modules.Medical.Ocr;
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
    }
}
