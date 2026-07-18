namespace FamilyHub.Modules.Medical.Medkits;

public record MedkitDto(Guid Id, Guid FamilyId, string Name, Guid CreatedByUserId, DateTime CreatedAt, int MedicationCount);

public record CreateMedkitRequest(string Name);

public record UpdateMedkitRequest(string Name);

public enum MedkitAccessResult { Success, Forbidden, NotFound }
