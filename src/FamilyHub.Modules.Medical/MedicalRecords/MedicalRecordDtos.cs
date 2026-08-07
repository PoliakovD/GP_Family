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
    IReadOnlyList<Guid> HiddenFamilyIds,
    Guid? FamilyDependentId,
    Guid? TargetUserId);

// Kind/FamilyDependentId/TargetUserId — последними и с дефолтом: существующие позиционные вызовы
// (тесты, ранее написанный код) остаются исходно совместимыми и создают личный анализ, как раньше.
public record CreateMedicalRecordRequest(
    string PersonName,
    DateOnly RecordDate,
    string? Doctor,
    string? Description,
    List<Guid>? HideFromFamilyIds,
    MedicalRecordKind Kind = MedicalRecordKind.Analysis,
    Guid? FamilyDependentId = null,
    Guid? TargetUserId = null);

public record FamilyIdsRequest(List<Guid> FamilyIds);

/// <summary>Результат in-memory поиска (этап 3, ADR-0003) — запись + релевантность запросу (0..1].</summary>
public record MedicalRecordSearchHit(MedicalRecordDto Record, double Score);

/// <summary>InvalidTarget — FamilyDependentId и TargetUserId заданы одновременно (взаимоисключимы).</summary>
public enum MedicalRecordAccessResult { Success, Forbidden, NotFound, InvalidTarget }
