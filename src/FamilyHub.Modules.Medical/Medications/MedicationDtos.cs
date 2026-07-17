namespace FamilyHub.Modules.Medical.Medications;

public record MedicationDto(Guid Id, Guid MedkitId, Guid FamilyId, string Name, string? Instructions, DateOnly? ExpiryDate, int Quantity, Guid CreatedByUserId, DateTime CreatedAt);

public record CreateMedicationRequest(string Name, string? Instructions, DateOnly? ExpiryDate, int Quantity);

public record UpdateMedicationRequest(string Name, string? Instructions, DateOnly? ExpiryDate, int Quantity);

public enum MedicationAccessResult { Success, Forbidden, NotFound }
