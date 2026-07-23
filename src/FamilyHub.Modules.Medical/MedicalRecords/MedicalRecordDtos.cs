namespace FamilyHub.Modules.Medical.MedicalRecords;

public record MedicalRecordDto(
    Guid Id,
    Guid OwnerUserId,
    string PersonName,
    DateOnly RecordDate,
    string? Doctor,
    string? Description,
    DateTime CreatedAt,
    IReadOnlyList<Guid> HiddenFamilyIds);

public record CreateMedicalRecordRequest(string PersonName, DateOnly RecordDate, string? Doctor, string? Description, List<Guid>? HideFromFamilyIds);

public record FamilyIdsRequest(List<Guid> FamilyIds);

/// <summary>Результат in-memory поиска (этап 3, ADR-0003) — запись + релевантность запросу (0..1].</summary>
public record MedicalRecordSearchHit(MedicalRecordDto Record, double Score);

public enum MedicalRecordAccessResult { Success, Forbidden, NotFound }
