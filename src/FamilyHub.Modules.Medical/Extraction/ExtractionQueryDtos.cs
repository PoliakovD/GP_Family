using FamilyHub.Domain.Enums;
using FamilyHub.Modules.Medical.Kb;

namespace FamilyHub.Modules.Medical.Extraction;

public record ExtractionStatusResponse(
    EnrichmentJobStatus Status, ExtractionStage Stage, int IndicatorCount, string? Error,
    int TotalFiles, int ProcessedFiles, DateTime CreatedAt, DateTime? CompletedAt);

/// <summary>ValueNumericText/KbAnalyteId — редизайн v2 (шкала-референс + панель справки).
/// RefLowText/RefHighText — либо `double.ToString(InvariantCulture)`, либо null; нечисловой
/// референс лежит в RefText (см. три точки записи: MedicalDocumentExtractionProcessor,
/// ExtractionQueryService, RecalculateIndicatorFlagsJob — все пишут через один и тот же
/// IndicatorFlagCalculator.Calculate/effLow-effHigh). Фронт вправе делать parseFloat без
/// нормализации запятых. Инвариант проверен тестом RefTextFieldsAreAlwaysParseable.</summary>
public record IndicatorDto(
    Guid Id, string AnalyteKey, string DisplayName, IndicatorFlag Flag, RefSource RefSource, SpecimenType Specimen, int Position,
    string ValueRaw, string? Unit, string? RefLowText, string? RefHighText, string? RefText,
    DateOnly RecordDate, Guid MedicalRecordId, Guid? SpecimenCustomId = null,
    string? ValueNumericText = null, Guid? KbAnalyteId = null);

public record IndicatorHistoryPoint(DateOnly RecordDate, string ValueRaw, string? ValueNumericText, IndicatorFlag Flag, Guid MedicalRecordId);

/// <summary>Возраст (на дату записи)/пол пациента — редизайн v2, панель справки. См.
/// PatientIdentityResolver.ResolveAsync (уже существующий резолвер, не новый).</summary>
public record PatientContextDto(int? AgeYears, Gender? Sex);

/// <summary>Ответ GET /api/indicators/{id}/article (редизайн v2, PR4-BE) — показатель + статья
/// справочника + персональная норма, одним запросом на клик по строке. Article=null — показатель
/// не привязан к KB (KbAnalyteId не проставлен каскадом при распознавании) — фронт показывает
/// «справка пока не заполнена», но панель всё равно открывается (значение+шкала есть всегда).
/// MatchedRefRangeIndex — индекс в Article.RefRanges, который нужно подсветить как "норма для
/// этого человека"; null, если Article=null или ни один диапазон не подошёл под пол/возраст.</summary>
public record IndicatorArticleResponse(
    IndicatorDto Indicator, PatientContextDto Patient, int? MatchedRefRangeIndex, KbAnalyteCard? Article, bool HistoryAvailable);

public record MyIndicatorSummary(
    string AnalyteKey, string DisplayName, SpecimenType Specimen, string ValueRaw, string? Unit,
    IndicatorFlag Flag, DateOnly LastRecordDate, Guid? SpecimenCustomId = null);

/// <summary>Форма MedicalRecord.SummaryJson, которую пишет LabSummarizer — используется только
/// для десериализации на чтении.</summary>
public record RecordSummaryResponse(string? PlainSummary, IReadOnlyList<LabSummaryDeviation> Deviations, IReadOnlyList<string> QuestionsForDoctor, string Disclaimer);

/// <summary>Назначенный препарат — ответ на чтение (GET .../conclusion, UX-редизайн). KbMedicationId
/// резолвится ЖИВЫМ поиском по kb.global_medications_kb при каждом чтении (см.
/// ExtractionQueryService.GetConclusionAsync), не хранится вместе с заключением — обогащение
/// справочника может завершиться уже после того, как заключение впервые сохранено, и тогда
/// сохранённая ссылка была бы навсегда null без отдельного бэкофилла.</summary>
public record PrescribedMedicationDto(string Name, string? DosageInstructions, Guid? KbMedicationId);

/// <summary>Заключение врача — ответ на чтение (GET .../conclusion, UX-редизайн). Собирается из
/// сохранённого FamilyHub.Modules.Medical.Extraction.VisitConclusion (см. ExtractionDtos.cs) +
/// живого резолва ссылок на справочник медикаментов.</summary>
public record VisitConclusionResponse(
    string? Diagnosis,
    string? Recommendations,
    string? Anamnesis,
    string? ProceduresPerformed,
    IReadOnlyList<PrescribedMedicationDto> PrescribedMedications);

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
