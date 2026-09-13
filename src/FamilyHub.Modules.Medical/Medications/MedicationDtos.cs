namespace FamilyHub.Modules.Medical.Medications;

/// <summary>EnrichmentPending — §5 плана «живой конвейер», зеркало IndicatorDto.EnrichmentPending:
/// медикамент промахнулся по справочнику, обогащение (MedicationEnrichmentJob) ещё не завершилось
/// — UI показывает чип «уточняем норму…» (см. MedicationService.GetForMedkitAsync).</summary>
public record MedicationDto(
    Guid Id, Guid MedkitId, Guid FamilyId, string Name, DateOnly? ExpiryDate, Dictionary<string, string> Data,
    Guid CreatedByUserId, DateTime CreatedAt, bool EnrichmentPending = false, string? EnrichmentLiveText = null);

public record CreateMedicationRequest(string Name, DateOnly? ExpiryDate, Dictionary<string, string>? Data);

public record UpdateMedicationRequest(string Name, DateOnly? ExpiryDate, Dictionary<string, string>? Data);

public enum MedicationAccessResult { Success, Forbidden, NotFound }
