namespace FamilyHub.Domain.Enums;

/// <summary>
/// Итог одного обращения к платному внешнему веб-поиску (Yandex/Brave, см. WebSearchCallLog) —
/// для админ-аудита "куда уходят деньги" (см. .claude/plans/ethereal-hugging-chipmunk.md, часть 2).
/// <see cref="CacheHit"/> — единственное значение, которое НЕ платное: пишется на ветке, где
/// провайдер вообще не вызывался (см. LabAnalyteSearchCacheService/MedicationSearchCacheService,
/// IsFresh) — без него нельзя ответить на вопрос "работает ли кэш" по одной этой таблице; строки
/// с Outcome != CacheHit — это и есть счётчик реально оплаченных вызовов.
/// </summary>
public enum WebSearchCallOutcome
{
    /// <summary>Провайдер ответил, суммаризатор получил непустой список сниппетов.</summary>
    Ok,
    /// <summary>Провайдер ответил успешно, но не вернул ни одного используемого источника.</summary>
    Empty,
    /// <summary>Yandex GenSearch — isAnswerRejected/problematicAnswer (см. YandexSearchProvider).</summary>
    Rejected,
    /// <summary>Yandex GenSearch — ни один источник не помечен used=true.</summary>
    NoUsedSources,
    /// <summary>HttpRequestException / не-успешный статус-код.</summary>
    HttpError,
    /// <summary>TaskCanceledException — таймаут запроса (EnrichmentOptions.TimeoutSeconds).</summary>
    Timeout,
    /// <summary>Платного вызова не было — использованы закэшированные результаты
    /// (LabAnalyteSearchCacheService/MedicationSearchCacheService.GetCachedAsync().IsFresh).</summary>
    CacheHit,
}
