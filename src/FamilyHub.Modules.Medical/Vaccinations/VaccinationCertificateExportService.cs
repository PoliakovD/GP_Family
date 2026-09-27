using FamilyHub.Infrastructure.Previews;

namespace FamilyHub.Modules.Medical.Vaccinations;

/// <summary>«Сертификат PDF» в шапке человека — снимок сделанных прививок на лету, без хранения
/// (в отличие от отчёта врачу): открыл, скачал, всё. Тот же Gotenberg, что у DoctorReportService.</summary>
public class VaccinationCertificateExportService(VaccinationService vaccinations, IGotenbergConverter gotenberg)
{
    public async Task<(VaccinationResult Result, byte[]? Pdf, string? FileName)> ExportAsync(
        Guid userId, string kind, Guid id, CancellationToken ct = default)
    {
        var (result, schedule) = await vaccinations.GetPersonScheduleAsync(userId, kind, id, ct);
        if (result != VaccinationResult.Success || schedule is null) return (VaccinationResult.NotFound, null, null);

        var pdf = await gotenberg.ConvertHtmlToPdfAsync(VaccinationCertificateHtmlRenderer.Render(schedule), null, ct);
        if (pdf is null || pdf.Length == 0) return (VaccinationResult.PdfUnavailable, null, null);

        var fileName = $"sertifikat-privivok-{DateOnly.FromDateTime(DateTime.UtcNow):yyyyMMdd}.pdf";
        return (VaccinationResult.Success, pdf, fileName);
    }
}
