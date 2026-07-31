using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Enrichment;
using FamilyHub.Modules.Medical.Medications;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Статус обогащения конкретного медикамента пользователя + ручной триггер «Уточнить в
/// справочнике» (GET/POST /api/medications/{id}/kb). Авторизация — тот же паттерн, что и
/// MedicationService (роль Member в семье медикамента), см. MedicationAccessResult.
/// </summary>
public class MedicationKbStatusService(
    AppDbContext db,
    IFamilyAccessService access,
    KbLookupService kbLookup,
    KbCatalogService catalog,
    IEnrichmentRequestService enrichment)
{
    public async Task<(MedicationAccessResult Result, MedicationKbResponse? Response)> GetStatusAsync(
        Guid medicationId, Guid userId, CancellationToken ct = default)
    {
        var (result, medication) = await LoadAuthorizedAsync(medicationId, userId, ct);
        if (result != MedicationAccessResult.Success) return (result, null);

        return (MedicationAccessResult.Success, await BuildStatusAsync(medication!, ct));
    }

    public async Task<MedicationAccessResult> RequestRefreshAsync(Guid medicationId, Guid userId, CancellationToken ct = default)
    {
        var (result, medication) = await LoadAuthorizedAsync(medicationId, userId, ct);
        if (result != MedicationAccessResult.Success) return result;

        await enrichment.RequestRefreshAsync(medication!, userId, ct);
        return MedicationAccessResult.Success;
    }

    private async Task<(MedicationAccessResult Result, Medication? Medication)> LoadAuthorizedAsync(
        Guid medicationId, Guid userId, CancellationToken ct)
    {
        var medication = await db.Medications.AsNoTracking().FirstOrDefaultAsync(m => m.Id == medicationId, ct);
        if (medication is null) return (MedicationAccessResult.NotFound, null);

        if (!await access.HasRoleAsync(userId, medication.FamilyId, FamilyRole.Member, ct))
            return (MedicationAccessResult.Forbidden, null);

        return (MedicationAccessResult.Success, medication);
    }

    private async Task<MedicationKbResponse> BuildStatusAsync(Medication medication, CancellationToken ct)
    {
        var normalizedName = MedicationNameNormalizer.Normalize(medication.Name);
        if (normalizedName.Length == 0) return new MedicationKbResponse(MedicationKbStatus.None, null, null);

        var lookup = await kbLookup.LookupAsync(normalizedName, ct);
        if (lookup.Kind == KbLookupKind.Hit)
        {
            var card = await catalog.GetByIdAsync(lookup.KbId!.Value, ct);
            return new MedicationKbResponse(MedicationKbStatus.Ready, card, null);
        }

        var candidate = lookup.Kind == KbLookupKind.Candidate
            ? new KbCandidate(lookup.KbId!.Value, lookup.DisplayName!, lookup.Score)
            : null;

        var job = await db.MedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.NormalizedName == normalizedName)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var status = job?.Status switch
        {
            EnrichmentJobStatus.Pending => MedicationKbStatus.Pending,
            EnrichmentJobStatus.Running => MedicationKbStatus.Running,
            EnrichmentJobStatus.Failed or EnrichmentJobStatus.Skipped => MedicationKbStatus.Failed,
            _ => MedicationKbStatus.None,
        };

        return new MedicationKbResponse(status, null, candidate);
    }
}
