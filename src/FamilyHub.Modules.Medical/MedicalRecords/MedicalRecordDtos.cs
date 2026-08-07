using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.MedicalRecords;

public record MedicalRecordDto(
    Guid Id,
    Guid OwnerUserId,
    MedicalRecordKind Kind,
    string PersonName,
    DateOnly RecordDate,
    string? Doctor,
    string? Description,
    ExtractionStatus ExtractionStatus,
    DateTime CreatedAt,
    IReadOnlyList<Guid> HiddenFamilyIds);

// Kind — последним и с дефолтом: существующие позиционные вызовы (тесты, ранее написанный код)
// остаются исходно совместимыми и создают анализ, как и раньше.
public record CreateMedicalRecordRequest(
    string PersonName,
    DateOnly RecordDate,
    string? Doctor,
    string? Description,
    List<Guid>? HideFromFamilyIds,
    MedicalRecordKind Kind = MedicalRecordKind.Analysis);

public record FamilyIdsRequest(List<Guid> FamilyIds);

/// <summary>Результат in-memory поиска (этап 3, ADR-0003) — запись + релевантность запросу (0..1].</summary>
public record MedicalRecordSearchHit(MedicalRecordDto Record, double Score);

public enum MedicalRecordAccessResult { Success, Forbidden, NotFound }
