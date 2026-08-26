using FamilyHub.Domain.Enums;

namespace FamilyHub.Modules.Medical.Extraction;

public record ExtractionStatusResponse(
    EnrichmentJobStatus Status, ExtractionStage Stage, int IndicatorCount, string? Error,
    int TotalFiles, int ProcessedFiles, DateTime CreatedAt, DateTime? CompletedAt);

public record IndicatorDto(
    Guid Id, string AnalyteKey, string DisplayName, IndicatorFlag Flag, RefSource RefSource, SpecimenType Specimen, int Position,
    string ValueRaw, string? Unit, string? RefLowText, string? RefHighText, string? RefText,
    DateOnly RecordDate, Guid MedicalRecordId, Guid? SpecimenCustomId = null);

public record IndicatorHistoryPoint(DateOnly RecordDate, string ValueRaw, string? ValueNumericText, IndicatorFlag Flag, Guid MedicalRecordId);

public record MyIndicatorSummary(
    string AnalyteKey, string DisplayName, SpecimenType Specimen, string ValueRaw, string? Unit,
    IndicatorFlag Flag, DateOnly LastRecordDate, Guid? SpecimenCustomId = null);

/// <summary>Форма MedicalRecord.SummaryJson, которую пишет LabSummarizer — используется только
/// для десериализации на чтении.</summary>
public record RecordSummaryResponse(string? PlainSummary, IReadOnlyList<LabSummaryDeviation> Deviations, IReadOnlyList<string> QuestionsForDoctor, string Disclaimer);

/// <summary>Ручная правка показателя (ошибка OCR) — только владелец записи, см. ExtractionQueryService.
/// Все поля — новое значение целиком (не патч), Flag пересчитывается сервером после сохранения по
/// тому же IndicatorFlagCalculator, что и при распознавании (референс из формы приоритетнее KB,
/// как и раньше — правка вручную это ещё один источник "из бланка").</summary>
public record UpdateIndicatorRequest(
    string DisplayName, string ValueRaw, string? Unit, SpecimenType Specimen,
    string? RefLowText, string? RefHighText, string? RefText, Guid? SpecimenCustomId = null);

public enum UpdateIndicatorResult { Success, NotFound, Forbidden, Conflict }

/// <summary>Ручное добавление показателя (UX-редизайн, задел сверх плана — без него редактируемая
/// таблица бесполезна для распространённого сценария "модель ничего не увидела на этой строке
/// бланка"). Та же форма, что UpdateIndicatorRequest — владелец записи, флаг считается тем же
/// компаратором, RefSource.Blank.</summary>
public record CreateIndicatorRequest(
    string DisplayName, string ValueRaw, string? Unit, SpecimenType Specimen,
    string? RefLowText, string? RefHighText, string? RefText, Guid? SpecimenCustomId = null);

public enum CreateIndicatorResult { Success, NotFound, Forbidden, Conflict }

public enum DeleteIndicatorResult { Success, NotFound, Forbidden }
