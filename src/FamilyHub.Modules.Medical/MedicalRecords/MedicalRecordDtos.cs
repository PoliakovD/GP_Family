namespace FamilyHub.Modules.Medical.MedicalRecords;

public record MedicalRecordDto(Guid Id, Guid OwnerUserId, string PersonName, DateOnly RecordDate, string? Doctor, string? Description, DateTime CreatedAt);

public record CreateMedicalRecordRequest(string PersonName, DateOnly RecordDate, string? Doctor, string? Description, List<Guid>? HideFromFamilyIds);

public record FamilyIdsRequest(List<Guid> FamilyIds);

public enum MedicalRecordAccessResult { Success, Forbidden, NotFound }
