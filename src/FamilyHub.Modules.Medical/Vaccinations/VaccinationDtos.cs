using FamilyHub.Domain.Enums;
using FamilyHub.Domain.Vaccinations;

namespace FamilyHub.Modules.Medical.Vaccinations;

public enum VaccinationResult { Success, NotFound, Forbidden, Invalid, PdfUnavailable }

public record VaccinationCertificateDto(Guid Id, List<AttachmentRefDto> Files);

public record SubjectDto(string Kind, Guid Id, string Name, bool IsSelf, bool CanEdit);

public record ScheduleItemDto(
    string SeriesCode, int DoseIndex, string Label, string SeriesName, string SeriesShortName,
    VaccineGroup Group, string Stage, VaccinationStatus Status,
    DateOnly? WindowFrom, DateOnly? WindowTo, Guid? RecordId, DateOnly? Date, bool IsRepeat);

/// <summary>Карточка «Стоит запланировать» в обзоре — один пункт графика одного человека.</summary>
public record AttentionCardDto(SubjectDto Subject, ScheduleItemDto Item);

/// <summary>Строка «Семья» в обзоре — счётчики для полосы прогресса.</summary>
public record SubjectSummaryDto(
    SubjectDto Subject, int Done, int DueSoon, int CanDo, int NoData, int Total, string? Hint);

public record OverviewDto(List<AttentionCardDto> Attention, List<SubjectSummaryDto> People);

public record DiseaseGroupDto(string Disease, List<ScheduleItemDto> Items);

public record PersonScheduleDto(
    SubjectDto Subject, int? AgeYears, int? AgeMonths, bool HasBirthDate,
    List<ScheduleItemDto> ByAge, List<DiseaseGroupDto> ByDisease, List<ScheduleItemDto> Custom);

public record AttachmentRefDto(Guid Id, string FileName, long SizeBytes, DateTime UploadedAt);

public record DiaryLinkDto(DateTime At, string Kind, string Text);

/// <summary>Одна доза в детальной панели серии — со своим статусом, файлами и (для своих прививок) связанными записями дневника.</summary>
public record DoseDetailDto(
    int DoseIndex, string Label, VaccinationStatus Status, DateOnly? WindowFrom, DateOnly? WindowTo,
    Guid? RecordId, DateOnly? Date, string? VaccineName, List<AttachmentRefDto> Files, List<DiaryLinkDto> DiaryEntries);

public record SeriesDetailDto(
    string SeriesCode, string SeriesName, IReadOnlyList<string> Diseases, VaccineGroup Group,
    string? About, string? Contraindications, IReadOnlyList<string> TradeNames, string? ReactionHint,
    List<DoseDetailDto> Doses);

/// <summary>Прививка не из календаря — своя отдельная карточка (детали строятся вокруг одной записи, не серии).</summary>
public record CustomVaccinationDetailDto(
    Guid Id, string Name, string? VaccineName, VaccinationKind Kind, DateOnly? Date,
    VaccinationDatePrecision? DatePrecision, List<AttachmentRefDto> Files, List<DiaryLinkDto> DiaryEntries, bool CanEdit);

public record CreateVaccinationRequest(
    string SubjectKind, Guid SubjectId,
    string? SeriesCode, int? DoseIndex, string? CustomName, string? VaccineName,
    VaccinationKind Kind, DateOnly? Date, VaccinationDatePrecision? DatePrecision,
    Guid? CertificateId, bool RequestWellbeingCheck);

public record UpdateVaccinationRequest(
    string? VaccineName, VaccinationKind Kind, DateOnly? Date, VaccinationDatePrecision? DatePrecision);

public record BulkMarkItem(string SeriesCode, int DoseIndex, VaccinationKind Kind, DateOnly? Date, VaccinationDatePrecision? DatePrecision);

public record BulkMarkRequest(string SubjectKind, Guid SubjectId, List<BulkMarkItem> Items, Guid? CertificateId);

public record BulkMarkResultDto(int Saved);

public record CatalogDoseDto(string Label, bool IsAgeBased);

public record CatalogSeriesDto(
    string Code, string Name, string ShortName, VaccineGroup Group, IReadOnlyList<string> Diseases,
    List<CatalogDoseDto> Doses, IReadOnlyList<string> TradeNames, string? About, string? Contraindications,
    string? ReactionHint, int? RepeatEveryYears, bool Seasonal);

public record AttentionCountResponse(int Count);
