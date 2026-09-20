using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Один фоновый прогон прогрева кэша веб-поиска из админки — зеркало <see cref="KbRebuildRun"/>
/// (инфраструктурное состояние, не медданные — живёт в схеме public, резюмируемый курсор прямо в
/// строке, переживает рестарт процесса). Ставится вручную, когда у облачного провайдера есть
/// невозвратный грантовый лимит на платный поиск: список популярных названий прогревается заранее,
/// чтобы кэш (MedicationSearchCache/LabAnalyteSearchCache) уже был наполнен, когда придёт живой
/// пользовательский запрос — тогда обогащение обойдётся без нового платного вызова.
///
/// Делает ТОЛЬКО платный поиск + запись в кэш (см. SearchCacheWarmupJob) — ни одного обращения к
/// локальной LLM: обычная задача обогащения тратит её дважды (LegitimacyGuardService,
/// Summarizer) на единственном воркере очереди "enrichment", и на сотнях имён это часы работы
/// впустую, если цель — быстро потратить сгорающий грант, а не наполнить справочник прямо сейчас.
/// Справочник наполнится позже бесплатно — обычный конвейер обогащения найдёт уже свежий кэш.
/// </summary>
public class SearchWarmupRun
{
    public Guid Id { get; set; }

    public WebSearchTopic Topic { get; set; }

    /// <summary>Биоматериал/источник — обязателен для LabAnalyte (см. валидацию в
    /// AdminSearchWarmupService.StartAsync), null для Medication.</summary>
    public Guid? SpecimenKbId { get; set; }

    /// <summary>Результат WarmupNameParser.Parse — [{raw, normalized}] после дедупа, сериализовано
    /// целиком один раз при старте: список не меняется в процессе прогона, только курсор по нему.</summary>
    public string NamesJson { get; set; } = string.Empty;

    /// <summary>Индекс следующего необработанного имени в NamesJson — резюмируемый курсор.</summary>
    public int Cursor { get; set; }

    public int TotalNames { get; set; }

    /// <summary>Реально оплаченных вызовов SearchAsync — единственная цифра, которая считает
    /// потраченные деньги за этот прогон.</summary>
    public int PaidCalls { get; set; }

    /// <summary>Пропущено молча — уже есть в справочнике (решение владельца: не трогаем).</summary>
    public int SkippedKbHit { get; set; }

    /// <summary>Пропущено — строка кэша уже свежая (CachedSearch.IsFresh), повторный платный
    /// вызов ничего бы не добавил.</summary>
    public int SkippedFreshCache { get; set; }

    /// <summary>Ошибок платного вызова, не остановивших прогон (см. SearchCacheWarmupJob —
    /// единичный сбой инкрементит счётчик и идёт дальше, не роняет прогон).</summary>
    public int Failures { get; set; }

    /// <summary>Бюджет прогона — прогон сам останавливается на PaidCalls >= MaxPaidCalls. Null —
    /// без ограничения (дойти до конца списка).</summary>
    public int? MaxPaidCalls { get; set; }

    public SearchWarmupStatus Status { get; set; } = SearchWarmupStatus.Running;

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Ставится админкой (POST .../warmup/cancel) — прогон проверяет флаг между именами
    /// и завершается на ближайшей проверке, не обрывается посреди сохранения.</summary>
    public bool CancelRequested { get; set; }
}
