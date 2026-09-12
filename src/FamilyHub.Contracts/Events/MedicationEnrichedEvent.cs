namespace FamilyHub.Contracts.Events;

/// <summary>
/// Справочник пополнен по итогам AI-конвейера обогащения (этап 4): OCR/ручной ввод → промах в
/// kb.global_medications_kb → веб-поиск → суммаризация локальным Qwen → запись в справочник.
/// Публикует MedicationEnrichmentProcessor; хендлер Notifications уведомляет пользователя,
/// сохранение медикамента которым запустило обогащение (только его — дедуп задач по
/// NormalizedName означает, что при параллельном сохранении того же препарата в другой семье
/// новая задача не создаётся вовсе, см. EnrichmentRequestService).
/// MedkitId — см. MedicationEnrichmentFailedEvent, тот же смысл (клик-через на аптечку, не на
/// сам справочник — KbId остаётся в событии отдельно, справочник глобален и не привязан к семье).
/// </summary>
public record MedicationEnrichedEvent(
    Guid JobId,
    Guid KbId,
    string DisplayName,
    Guid RequestedByUserId,
    Guid FamilyId,
    Guid? MedkitId);
