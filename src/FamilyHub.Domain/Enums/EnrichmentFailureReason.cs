namespace FamilyHub.Domain.Enums;

/// <summary>
/// Структурная причина отказа задачи обогащения/извлечения (Status=Failed) — раньше единственным
/// местом хранения причины была свободная строка Job.Error, по которой нельзя было ни
/// сгруппировать падения в админке («инбокс Требует внимания»), ни отличить программно «нет
/// доверенных сниппетов» (чинится добавлением домена) от «модель не сослалась на источник»
/// (чинится иначе). Error остаётся человекочитаемым текстом для конкретной задачи, FailureReason —
/// машиночитаемая классификация поверх него. None — задача не падала (Pending/Running/Completed).
/// </summary>
public enum EnrichmentFailureReason
{
    None = 0,

    /// <summary>Не прошла ILegitimacyGuardService.CheckAsync — подозрение на промпт-инъекцию/
    /// нелегитимный текст в SourceDisplayName.</summary>
    Legitimacy = 1,

    /// <summary>Не прошла IAnalytePlausibilityGuardService.CheckAsync (только
    /// EnrichmentRequestOrigin.ManualEntry) — показатель не похож на реальный лабораторный анализ.</summary>
    Plausibility = 2,

    /// <summary>После фильтрации по EnrichmentTrustedDomain + override'ам не осталось ни одного
    /// сниппета — суммаризатор не вызывался вовсе. Чинится добавлением домена в доверенные или
    /// точечным override конкретного URL (см. EnrichmentSnippetFilter).</summary>
    NoTrustedSnippets = 3,

    /// <summary>Сниппеты были, суммаризатор ответил, но не сослался ни на один источник
    /// (антигаллюцинационный гейт) — усомниться в качестве самих сниппетов, не в списке доменов.</summary>
    NoSourcesCited = 4,

    /// <summary>Модель не вернула структурированный ответ либо не извлекла ни одного
    /// содержательного поля — технический или смысловой сбой самой суммаризации.</summary>
    SummarizerFailed = 5,

    /// <summary>KbIsolationGuard нашёл подозрение на персональный контекст в payload — запись в
    /// общий справочник отклонена (см. KbWriter/LabAnalyteKbWriter).</summary>
    IsolationViolation = 6,

    /// <summary>Терминальный отказ после исчерпания ретраев, вызванный LmStudioUnavailableException —
    /// технический сбой локального сервера LLM, не смысловой отказ.</summary>
    LmStudioUnavailable = 7,

    /// <summary>Терминальный отказ после исчерпания ретраев, вызванный сбоем внешнего провайдера
    /// поиска (Brave/Yandex) — зарезервировано на случай появления отдельного типа исключения.</summary>
    ProviderFailed = 8,

    /// <summary>Терминальный отказ по необработанному исключению без более точной классификации.</summary>
    Unknown = 9,
}
