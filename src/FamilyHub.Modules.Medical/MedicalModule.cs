using FamilyHub.Modules.Medical.Attachments;
using FamilyHub.Modules.Medical.MedicalRecords;
using FamilyHub.Modules.Medical.Medications;
using FamilyHub.Modules.Medical.Medkits;
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
        return services;
    }

    public static void MapMedicalModule(this IEndpointRouteBuilder app)
    {
        app.MapMedkitEndpoints();
        app.MapMedicationEndpoints();
        app.MapMedicalRecordEndpoints();
        app.MapAttachmentEndpoints();
    }
}
