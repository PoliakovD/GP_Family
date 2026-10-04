using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Enrichment;

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

    /// <summary>Тема кэша поиска: препараты (аптечка и заключения врача) делят одну таблицу кэша.</summary>
    public static WebSearchTopic TopicOf(string kind) => kind == LabAnalyte ? WebSearchTopic.LabAnalyte : WebSearchTopic.Medication;
}

/// <summary>Стадия задачи в очереди: поиск ждёт одобрения / результат ждёт проверки.</summary>
public static class ReviewStages
{
    public const string Search = "search";
    public const string Result = "result";
}

/// <summary>Строка единого Inbox (ADR-0018): и платные поиски, и результаты с низкой уверенностью.
/// Confidence/Threshold — по стадии (запрос/результат). BelowThreshold — ниже порога или оценки нет: такие строки
/// подсвечиваются и идут первыми. Origin — откуда задача: "extraction" | "manual" | "maintenance" (показатели),
/// "medkit" (аптечка), "visit" (заключение врача). HasTwins — для показателей: у другого биоматериала есть свежий
/// кэш того же названия (бесплатная альтернатива платному поиску).</summary>
public record ReviewInboxItemDto(
    Guid Id, string Kind, string Stage, string Name, string? Specimen, string? QueryText,
    double? Confidence, string? ConfidenceReason, double Threshold, bool BelowThreshold,
    string Origin, DateTime CreatedAt, bool HasTwins, bool HasNote);

public record ReviewInboxResponse(List<ReviewInboxItemDto> Rows, int Total, int Searches, int Results);

/// <summary>Черновик в форме записи справочника: PayloadJson — ровно тот jsonb, что ляжет в kb (его
/// показывает/правит admin-payload-editor), Aliases — уже нормализованные.</summary>
public record ReviewDraftDto(
    string NormalizedName, string DisplayName, string PayloadJson, List<string> Aliases, string Source);

/// <summary>Текущая запись справочника — для подсветки отличий черновика. Null, если записи ещё нет.
/// VerificationStatus/VerifiedAt — внутренний маркер админки (пользователям не отдаётся).</summary>
public record ReviewCurrentKbDto(
    Guid Id, string DisplayName, string PayloadJson, List<string> Aliases, List<string> LockedFields, DateTime UpdatedAt,
    KbVerificationStatus VerificationStatus = KbVerificationStatus.AiUnverified, DateTime? VerifiedAt = null,
    bool VerificationStale = false);

/// <summary>Источник (сниппет кэша) в карточке: Enabled — итоговое решение (override либо доверенный домен, либо
/// ручной/закреплённый); UntrustedWarning — у ручного/закреплённого сниппета домен вне доверенных (предупреждение,
/// не блокировка); UsedInDraft — на него опирается черновик.</summary>
public record ReviewSourceDto(
    int Index, string Title, string Url, string Text, string? Domain, bool TrustedByDomain, bool Enabled, bool? Override,
    SnippetOrigin Origin, string? Kind, string? Note, bool Pinned, bool UsedInDraft, bool UntrustedWarning);

/// <summary>Строка кэша поиска, с которой работает карточка: CacheId null — строки ещё нет (её создаёт «ensure»).</summary>
public record ReviewCacheDto(
    Guid? CacheId, string Topic, string? Provider, DateTime? LastUpdatedAt, DateTime? CanBeUpdatedAfter, bool Fresh,
    int SnippetCount, int ManualCount, string? SearchGroupKey, string? QueryLabel);

/// <summary>«Двойник» — свежий кэш того же названия у другой группы биоматериалов (бесплатная альтернатива платному поиску).</summary>
public record ReviewTwinDto(
    Guid CacheId, string Specimen, string? SearchGroupKey, string Provider, DateTime LastUpdatedAt, int SnippetCount);

/// <summary>Атрибуция полей черновика к источникам (ADR-0018): FieldSources — поле → URL сниппетов; FieldsWithoutSource —
/// непустые поля без источника (подсветка жёлтым); Available=false — модель не вернула атрибуцию (старый промпт),
/// подсвечивать нечего.</summary>
public record ReviewFieldSourcesDto(bool Available, Dictionary<string, List<string>> FieldSources, List<string> FieldsWithoutSource);

/// <summary>Деталь задачи очереди (обе стадии). Draft/Current/FieldSources — только на стадии результата;
/// Twins/QueryText — на стадии поиска. Note — заметка админа к решению.</summary>
public record ReviewItemDetailDto(
    Guid Id, string Kind, string Stage, string Name, string? Specimen, string Origin, string? Provider, DateTime CreatedAt,
    string QueryText, double? QueryConfidence, string? QueryConfidenceReason, double QueryThreshold, bool QueryBelowThreshold,
    double? ResultConfidence, string? ResultConfidenceReason, double ResultThreshold, bool ResultBelowThreshold,
    ReviewDraftDto? Draft, ReviewCurrentKbDto? Current, ReviewFieldSourcesDto? FieldSourceInfo,
    ReviewCacheDto Cache, List<ReviewSourceDto> Sources, List<ReviewTwinDto> Twins, string? Note,
    ReviewKbMatchDto? KbMatch = null);

/// <summary>Почему поиск показателя ждёт одобрения, хотя статья справочника находится (см. ReviewKbMatchDto).</summary>
public static class ReviewKbMatchReasons
{
    /// <summary>Статья есть, но в ней нет нормы в единицах бланка (Units) — поиск за единицами.</summary>
    public const string UnitGap = "unit-gap";

    /// <summary>Переобогащение существующей статьи (обслуживание справочника, «Переобогатить»).</summary>
    public const string Reenrich = "reenrich";

    /// <summary>Статья стала находимой уже после парковки — поиск не нужен, закроется перепроверкой.</summary>
    public const string Found = "found";

    /// <summary>Похожая статья ниже порога автопривязки — возможно, тот же показатель (объединить/синоним).</summary>
    public const string Candidate = "candidate";
}

/// <summary>Статья справочника, найденная для запаркованного поиска показателя (только стадия поиска). Reason — см.
/// <see cref="ReviewKbMatchReasons"/>; Score — уверенность совпадения (1 — точный ключ/синоним); Units — единицы бланков.</summary>
public record ReviewKbMatchDto(Guid KbId, string DisplayName, string Reason, double Score, string? Units);

/// <summary>Карточка сущности вне очереди (вариант C): запись справочника + кэш источников + задача в очереди, если есть.</summary>
public record ReviewEntityDto(
    string Kind, ReviewCurrentKbDto Current, string? Specimen, ReviewCacheDto Cache, List<ReviewSourceDto> Sources,
    Guid? QueueJobId, string? QueueStage);

/// <summary>Предложение новой версии записи: пересуммаризация по текущему набору сниппетов кэша без записи в kb —
/// админ принимает его правкой в редакторе (с локами и журналом) либо отбрасывает.</summary>
public record ReviewResummarizePreviewDto(
    string DisplayName, string PayloadJson, List<string> Aliases, double? Confidence, string? ConfidenceReason,
    ReviewFieldSourcesDto FieldSourceInfo, List<ReviewSourceDto> UsedSources);

/// <summary>Одобрение поиска. Mode: "search" (по умолчанию — платный поиск) | "use-cache" (без платного поиска: набор из
/// кэша — собственного либо скопированного у двойника TwinCacheId). QueryText — правка текста запроса (для mode=search).</summary>
public record ApproveSearchRequest(string? QueryText = null, string? Mode = null, Guid? TwinCacheId = null, string? Note = null);

public record RejectRequest(string? Reason, string? Note = null);

public record ReviewItemRef(string Kind, Guid Id);

/// <summary>Элемент массового одобрения: QueryText — правка текста запроса этой строки (null — как предложено).</summary>
public record BulkApproveItem(string Kind, Guid Id, string? QueryText);

public record BulkApproveSearchesRequest(List<BulkApproveItem> Items);

public record BulkRejectRequest(List<ReviewItemRef> Items, string? Reason);

public record BulkReviewResponse(int ProcessedCount, List<ReviewItemRef> FailedItems);

/// <summary>Итог «Перепроверить по справочнику»: сколько запаркованных поисков закрыто — показатель уже есть в справочнике.</summary>
public record RecheckKbResponse(int Resolved);

/// <summary>Одобрение результата. Все поля необязательны: без правок в kb уходит черновик как есть. PayloadJson/DisplayName/
/// Aliases — правки админа; реально изменённые относительно черновика поля попадают в LockedFields записи (как при ручной
/// правке каталога), чтобы следующее автообогащение их не перетёрло.</summary>
public record ApproveResultRequest(string? PayloadJson, string? DisplayName, List<string>? Aliases, string? Note = null);

public record SetReviewNoteRequest(string? Note);

public record AddManualSnippetRequest(string? Kind, string? Url, string? Title, string? Text, string? Note);

public record SnippetOverrideRequest(string Url, bool? Enabled);

/// <summary>Правка сниппета набора: заголовок, текст (короткая выдержка), заметка. URL — ключ сниппета, не меняется.</summary>
public record EditSnippetRequest(string Url, string? Title, string? Text, string? Note);

/// <summary>Кандидат «взять из готового кэша»: любая непустая строка кэша той же темы (другое написание, торговое
/// название/МНН, похожий показатель). Specimen — для показателей. Fresh — кэш ещё не устарел (на выбор не влияет).</summary>
public record ReviewCacheCandidateDto(
    Guid CacheId, string Topic, string NormalizedName, string? Specimen, string Provider, DateTime LastUpdatedAt, bool Fresh,
    int SnippetCount, int ManualCount);

/// <summary>Импорт сниппетов кандидата в набор задачи. Urls — выбранные сниппеты (null — все).</summary>
public record ImportReviewCacheRequest(Guid SourceCacheId, List<string>? Urls);

public record ImportReviewCacheResponse(Guid CacheId, int Imported);

public record SnippetPinRequest(string Url, bool Pinned);

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

/// <summary>Итог действия, возвращающего значение (ensure кэша, добавление сниппета).</summary>
public record ReviewActionOutcome<T>(ReviewActionResult Result, T? Value = default, string? Message = null);
