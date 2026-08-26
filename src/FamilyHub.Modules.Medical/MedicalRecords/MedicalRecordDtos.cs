using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.MedicalRecords;

/// <summary>PersonName — резолвится на чтение из OwnerUserId/FamilyDependentId/TargetUserId
/// (см. MedicalRecordService.ResolvePersonNamesAsync), не хранится (v2, "пациента убрать").</summary>
public record MedicalRecordDto(
    Guid Id,
    Guid OwnerUserId,
    MedicalRecordKind Kind,
    string PersonName,
    DateOnly RecordDate,
    string? Doctor,
    string? Title,
    string? Description,
    ExtractionStatus ExtractionStatus,
    DateTime CreatedAt,
    IReadOnlyList<Guid> HiddenFamilyIds,
    Guid? FamilyDependentId,
    Guid? TargetUserId);

// Kind/FamilyDependentId/TargetUserId — последними и с дефолтом: существующие позиционные вызовы
// (тесты, ранее написанный код) остаются исходно совместимыми и создают личный анализ, как раньше.
// PersonName убран (v2) — идентичность пациента выражается целиком через
// FamilyDependentId/TargetUserId/владельца, отдельного текстового поля больше нет.
public record CreateMedicalRecordRequest(
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
