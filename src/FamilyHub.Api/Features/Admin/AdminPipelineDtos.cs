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
/// дискриминатор для PUT retry ниже ("lab-analyte"/"medication"/"visit-medication"/"extraction").</summary>
public record PipelineJobDto(
    Guid Id, string Type, string DisplayName, string Status, int Attempts, string? Error,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt);

public record PipelineJobListResponse(List<PipelineJobDto> Rows, int Total);
