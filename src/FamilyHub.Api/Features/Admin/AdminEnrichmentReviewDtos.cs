namespace FamilyHub.Api.Features.Admin;

/// <summary>Вид задачи в очереди «Одобрение» — те же дискриминаторы, что у AdminPipelineEndpoints
/// ("lab-analyte"/"medication"/"visit-medication"), чтобы фронт переиспользовал подписи.</summary>
public static class ReviewKinds
{
    public const string LabAnalyte = "lab-analyte";
    public const string Medication = "medication";
    public const string VisitMedication = "visit-medication";

    public static readonly string[] All = [LabAnalyte, Medication, VisitMedication];

    public static bool IsValid(string? kind) => kind is LabAnalyte or Medication or VisitMedication;
}

/// <summary>Строка очереди «Поиски»: платный поиск ждёт одобрения. QueryText — предложенный/правленый
/// текст запроса (по умолчанию нормализованное имя). BelowThreshold — уверенность стража ниже порога
/// своего вида (или null) — такие строки подсвечиваются и идут первыми. Origin — откуда пришла задача:
/// "extraction" | "manual" | "maintenance" (показатели), "medkit" (аптечка), "visit" (заключение врача).</summary>
public record ReviewSearchItemDto(
    Guid Id, string Kind, string Name, string? Specimen, string QueryText,
    double? QueryConfidence, string? QueryConfidenceReason, double Threshold, bool BelowThreshold,
    string Origin, DateTime CreatedAt);

public record ReviewSearchListResponse(List<ReviewSearchItemDto> Rows, int Total);

/// <summary>Строка очереди «Результаты»: черновик с уверенностью суммаризатора ниже порога.</summary>
public record ReviewResultItemDto(
    Guid Id, string Kind, string Name, string? Specimen,
    double? ResultConfidence, string? ResultConfidenceReason, double Threshold, bool BelowThreshold,
    string? Provider, DateTime CreatedAt);

public record ReviewResultListResponse(List<ReviewResultItemDto> Rows, int Total);

/// <summary>Черновик в форме записи справочника: PayloadJson — ровно тот jsonb, что ляжет в kb (его
/// показывает/правит admin-payload-editor), Aliases — уже нормализованные.</summary>
public record ReviewDraftDto(
    string NormalizedName, string DisplayName, string PayloadJson, List<string> Aliases, string Source);

/// <summary>Текущая запись справочника — для подсветки отличий черновика. Null, если записи ещё нет.</summary>
public record ReviewCurrentKbDto(
    Guid Id, string DisplayName, string PayloadJson, List<string> Aliases, List<string> LockedFields, DateTime UpdatedAt);

/// <summary>Сниппет, на котором построен черновик. Used — на него сослалась модель (usedSourceIndexes).</summary>
public record ReviewSnippetDto(int Index, string Title, string Url, string Text, bool Used);

public record ReviewResultDetailDto(
    Guid Id, string Kind, string Name, string? Specimen,
    double? ResultConfidence, string? ResultConfidenceReason, double Threshold, bool BelowThreshold,
    double? QueryConfidence, string? QueryConfidenceReason,
    string? Provider, DateTime CreatedAt,
    ReviewDraftDto Draft, ReviewCurrentKbDto? Current, List<ReviewSnippetDto> Snippets);

/// <summary>QueryText — правка текста запроса перед одобрением (необязательна: без неё уйдёт предложенный).</summary>
public record ApproveSearchRequest(string? QueryText);

public record RejectRequest(string? Reason);

public record ReviewItemRef(string Kind, Guid Id);

/// <summary>Массовое действие над поисками. Queries — необязательные правки текста запроса по id.</summary>
public record BulkApproveSearchesRequest(List<ReviewItemRef> Items);

public record BulkRejectRequest(List<ReviewItemRef> Items, string? Reason);

public record BulkReviewResponse(int ProcessedCount, List<ReviewItemRef> FailedItems);

/// <summary>Одобрение результата. Все поля необязательны: без правок в kb уходит черновик как есть.
/// PayloadJson/DisplayName/Aliases — правки админа; реально изменённые относительно черновика поля
/// попадают в LockedFields записи (как при ручной правке каталога), чтобы следующее автообогащение
/// их не перетёрло.</summary>
public record ApproveResultRequest(string? PayloadJson, string? DisplayName, List<string>? Aliases);

public record ReviewQueueCountsDto(int Searches, int Results, Dictionary<string, int> SearchesByKind, Dictionary<string, int> ResultsByKind);

public record EnrichmentReviewConfigDto(
    double MedicationQueryMinConfidence, double AnalyteQueryMinConfidence,
    double MedicationResultMinConfidence, double AnalyteResultMinConfidence,
    DateTime? UpdatedAt);

public record SetEnrichmentReviewConfigRequest(
    double MedicationQueryMinConfidence, double AnalyteQueryMinConfidence,
    double MedicationResultMinConfidence, double AnalyteResultMinConfidence);

/// <summary>Итог одиночного действия очереди — маппится эндпоинтами в HTTP-коды.</summary>
public enum ReviewActionResult
{
    Ok,
    NotFound,

    /// <summary>Задача не в том статусе (уже обработана другим админом/кликом) — 409.</summary>
    WrongStatus,

    /// <summary>Невалидный вход (пустой запрос, невалидный JSON, подозрение на персональный контекст) — 400.</summary>
    Invalid,

    /// <summary>Не удалось получить ответ модели (пересуммаризация) — 502.</summary>
    UpstreamFailed,
}

public record ReviewActionOutcome(ReviewActionResult Result, string? Message = null)
{
    public static ReviewActionOutcome Ok { get; } = new(ReviewActionResult.Ok);
}
