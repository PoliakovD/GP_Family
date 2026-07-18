namespace FamilyHub.Modules.Medical.Medications;

public record MedicationDto(Guid Id, Guid MedkitId, Guid FamilyId, string Name, DateOnly? ExpiryDate, Dictionary<string, string> Data, Guid CreatedByUserId, DateTime CreatedAt);

public record CreateMedicationRequest(string Name, DateOnly? ExpiryDate, Dictionary<string, string>? Data);

public record UpdateMedicationRequest(string Name, DateOnly? ExpiryDate, Dictionary<string, string>? Data);

public enum MedicationAccessResult { Success, Forbidden, NotFound }
