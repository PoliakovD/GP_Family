using FamilyHub.Domain.Enums;

namespace FamilyHub.Api.Features.Admin;

/// <summary>Один шаг одного пайплайна с текущим состоянием (управление enrich-пайплайном из
/// админки, §2 плана) — зеркало PipelineCatalog.Steps + PipelineStepConfig. IsMandatory=true
/// шаги нельзя выключить (см. PUT ниже — вернёт 409).</summary>
public record PipelineStepDto(
    string PipelineKey, string StepKey, string Description, bool IsMandatory, bool IsEnabled, string? PromptKey);

public record SetStepEnabledRequest(bool IsEnabled);

/// <summary>Один слот промпта с активной версией (если она есть в БД) — при отсутствии активной
/// версии конвейер использует захардкоженный фолбэк в коде (см. PromptProvider), ActiveVersion
/// в этом случае null.</summary>
public record PromptSlotDto(string Key, string Description, int? ActiveVersion, DateTime? ActiveVersionCreatedAt);

public record PromptVersionDto(Guid Id, int Version, bool IsActive, string? Note, DateTime CreatedAt, string Body);

public record CreatePromptVersionRequest(string Body, string? Note);

public record DryRunRequest(string PromptKey, string? BodyOverride, string UserText);

public record DryRunResponse(bool Success, string? Error, Dictionary<string, System.Text.Json.JsonElement>? Payload);

/// <summary>Одна строка задачи любого из четырёх конвейеров обогащения/извлечения — раньше видны
/// были только через сырой Hangfire-дашборд (пересборка enrich-пайплайна, §2.3 плана). Type —
/// дискриминатор для PUT retry ниже ("lab-analyte"/"medication"/"visit-medication"/"extraction").
/// FailureReason — строкой (как Status), см. EnrichmentFailureReason; null, пока задача не падала.</summary>
public record PipelineJobDto(
    Guid Id, string Type, string DisplayName, string Status, int Attempts, string? Error,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, string? FailureReason = null);

public record PipelineJobListResponse(List<PipelineJobDto> Rows, int Total);

/// <summary>Карточка одной задачи — то, что открывается в боковой панели админки («Требует
/// внимания» → карточка, и список задач → карточка). NormalizedName/SpecimenKbId/SpecimenDisplayName
/// null для extraction (нет понятия справочника/сниппетов у этого конвейера); SearchCache — та же
/// строка кэша поиска, которую вернул бы GET /enrichment/search-cache/{id} (общий AdminEnrichmentEndpoints.BuildDetail),
/// null, пока строки кэша ещё нет (например, задача упала на гейте легитимности до первого поиска)
/// или для extraction. TrustedDomains — активный список темы, чтобы UI пометил недоверенные строки
/// кнопкой «доверить домен» без отдельного запроса.</summary>
public record PipelineJobDetailDto(
    Guid Id, string Type, string DisplayName, string Status, int Attempts, string? Error,
    string? FailureReason, DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt,
    string? NormalizedName, Guid? SpecimenKbId, string? SpecimenDisplayName,
    string? Origin, bool Force, string? Provider, DateTime? ExternalSearchAt, bool IsTransientFailure,
    Guid? KbId, SearchCacheDetailDto? SearchCache, List<TrustedDomainDto> TrustedDomains);

/// <summary>Один элемент правки override в resolve-and-retry — Enabled=null снимает override
/// (см. EnrichmentSnippetFilter/SetSnippetOverrideAsync), тот же смысл, что у SetSnippetOverrideRequest.</summary>
public record SnippetOverrideItem(string Url, bool? Enabled);

/// <summary>Тело «Применить и перезапустить» — применяет override'ы и/или добавляет домены в
/// доверенные ОДНИМ запросом, затем сбрасывает задачу в Pending и ставит её в очередь заново.
/// Оба списка необязательны и независимы: можно прислать только домены (без override'ов конкретных
/// URL) или наоборот — это ровно то же самое, что сделать оба действия по отдельности на старых
/// вкладках, но без переключения между ними.</summary>
public record ResolveAndRetryRequest(List<SnippetOverrideItem>? Overrides, List<string>? TrustDomains);

/// <summary>Массовый перезапуск — потолок числа id проверяется в эндпоинте (см. class doc
/// AdminPipelineEndpoints). NotFoundIds — id, для которых задача с таким Type не найдена (не
/// считаются ошибкой всего запроса — остальные всё равно перезапускаются).</summary>
public record BulkRetryRequest(string Type, List<Guid> Ids);

public record BulkRetryResponse(int RetriedCount, List<Guid> NotFoundIds);

/// <summary>Массовое удаление — тот же дискриминатор Type/Ids, что BulkRetryRequest, но убирает
/// строки насовсем, не перезапускает. Для задач, упавших без структурной причины (FailureReason
/// не проставлен — упали до этой правки, см. AttentionReasonDto), а также любых устаревших/более
/// не интересных Failed-строк, засоряющих список.</summary>
public record BulkDeleteRequest(string Type, List<Guid> Ids);

public record BulkDeleteResponse(int DeletedCount, List<Guid> NotFoundIds);

/// <summary>Итог purge-unclassified — по одному счётчику на конвейер плюс общий, чтобы админ видел,
/// откуда именно были удалены строки.</summary>
public record PurgeUnclassifiedResponse(
    int LabAnalyteDeleted, int MedicationDeleted, int VisitMedicationDeleted, int ExtractionDeleted, int TotalDeleted);
