using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.MedicalRecords;

/// <summary>PersonName — резолвится на чтение из OwnerUserId/FamilyDependentId/TargetUserId
/// (см. MedicalRecordService.ResolvePersonNamesAsync), не хранится (v2, "пациента убрать").
/// AttachmentCount/UnrecognizedAttachmentCount/IndicatorCount (UX-редизайн) — считаются одним
/// GroupBy по странице записей на сервере, чтобы фронт решал, показывать ли «Распознать» и
/// заголовок «Файлы (N)» БЕЗ отдельного GET /attachments на каждую запись (был N+1 в refresh()).</summary>
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
    Guid? TargetUserId,
    int AttachmentCount = 0,
    int UnrecognizedAttachmentCount = 0,
    int IndicatorCount = 0);

/// <summary>Постраничный ответ (UX-редизайн) — используется и для списка мед-записей, и для
/// глобального поиска. TotalPages вычисляется на сервере, а не на фронте, чтобы не дублировать
/// округление (Math.Ceiling) в двух местах.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages)
{
    public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, int totalCount) =>
        new(items, page, pageSize, totalCount, totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize));
}

/// <summary>Серверные фильтры списка мед-записей (UX-редизайн) — Doctor/Query требуют in-memory
/// пути (см. MedicalRecordService.GetVisibleRecordsAsync), Doctor — [Encrypted], SQL-фильтр по
/// нему невозможен (ADR-0002); Query — тот же принцип, что и в SearchAsync.</summary>
public record MedicalRecordFilter(
    MedicalRecordKind? Kind = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? FamilyDependentId = null,
    Guid? TargetUserId = null,
    bool SelfOnly = false,
    string? Doctor = null,
    string? Query = null,
    int Page = 1,
    int PageSize = 15)
{
    public const int MaxPageSize = 100;
}

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
