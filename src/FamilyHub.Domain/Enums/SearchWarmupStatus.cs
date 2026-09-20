namespace FamilyHub.Domain.Enums;

/// <summary>Статус прогона прогрева кэша веб-поиска (SearchWarmupRun) — админка ставит платные
/// вызовы поиска по списку имён заранее (пока действует грантовый лимит облака), не дожидаясь
/// живого пользовательского спроса. См. class doc SearchCacheWarmupJob.</summary>
public enum SearchWarmupStatus
{
    /// <summary>Прогон активен — либо выполняется прямо сейчас, либо ждёт своей очереди
    /// самопродолжения (см. SearchCacheWarmupJob.BatchSize).</summary>
    Running = 0,

    /// <summary>Приостановлен вентилем платного поиска (Enrichment §2, этап 2 плана) — курсор
    /// сохранён, продолжится автоматически при открытии вентиля.</summary>
    Paused = 1,

    /// <summary>Дошёл до конца списка имён (или упёрся в MaxPaidCalls) без отмены.</summary>
    Completed = 2,

    /// <summary>Упал на необработанном исключении — единичные сбои поиска сами по себе не
    /// прерывают прогон (см. SearchCacheWarmupJob), Failed — только на инфраструктурной ошибке.</summary>
    Failed = 3,

    /// <summary>Остановлен вручную из админки (CancelRequested) — курсор сохранён, но
    /// самопродолжение не планируется.</summary>
    Cancelled = 4,
}
