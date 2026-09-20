using FamilyHub.Domain.Enums;

namespace FamilyHub.Api.Features.Admin;

/// <summary>Names — сырой текст textarea, одна строка на имя (см. WarmupNameParser.Parse).
/// MaxPaidCalls — бюджет платных вызовов на этот прогон, null = без ограничения (дойти до конца
/// списка). SpecimenKbId обязателен при Topic=LabAnalyte — см. валидацию в
/// AdminSearchWarmupService.StartAsync.</summary>
public record StartWarmupRequest(WebSearchTopic Topic, Guid? SpecimenKbId, string Names, int? MaxPaidCalls);

/// <summary>Снимок последнего прогона прогрева (текущего или уже завершившегося — тот же приём,
/// что KbRebuildStatusDto: UI показывает финальный результат сразу после остановки поллинга).
/// Status: "Running" | "Paused" | "Completed" | "Failed" | "Cancelled" | null (ни разу не запускался).</summary>
public record WarmupStatusDto(
    Guid? RunId, string? Status, WebSearchTopic? Topic, string? SpecimenDisplayName,
    int TotalNames, int Cursor, int PaidCalls, int SkippedKbHit, int SkippedFreshCache, int Failures,
    int? MaxPaidCalls, DateTime? StartedAt, DateTime? FinishedAt, string? LastError);
