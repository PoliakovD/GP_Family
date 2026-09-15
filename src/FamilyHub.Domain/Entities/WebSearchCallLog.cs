using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Аудит-лог обращений к платному внешнему веб-поиску (Yandex GenSearch / Brave, см.
/// IMedicationSearchProvider) — одна строка на КАЖДЫЙ вызов SearchAsync, включая кэш-хиты
/// (Outcome=CacheHit, см. WebSearchCallOutcome), пишется самими провайдерами и процессорами (см.
/// WebSearchCallLogger). Схема public — инфраструктурное состояние, не персональные данные: наружу
/// и так уходит только нормализованное название (ADR-0005 §1), тот же выбор, что у
/// EncryptionRotationRun. Ретеншн — AuditRetentionJob (по умолчанию 180 дней).
///
/// До этой таблицы восстановить "сколько заплачено и за что" было нельзя — единственным следом
/// были job.ExternalSearchAt/Provider и строка кэша (kb.*_search_cache), которая хранит только
/// ПОСЛЕДНИЙ результат по ключу и перезаписывается при каждом обновлении.
/// </summary>
public class WebSearchCallLog
{
    public Guid Id { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>"Yandex"/"Brave"/"Null" — IMedicationSearchProvider.Name, не enum: провайдеров
    /// добавляют строкой уже сейчас (см. class doc YandexSearchProvider/BraveSearchProvider),
    /// заводить сюда третий enum ради лога избыточно.</summary>
    public string Provider { get; set; } = string.Empty;

    public WebSearchTopic Topic { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Только для Topic=LabAnalyte — источник (кровь/моча/...), участвующий в ключе кэша
    /// (LabAnalyteSearchCache.SpecimenKbId) и в тексте запроса.</summary>
    public string? SpecimenDisplayName { get; set; }

    /// <summary>Буквально то, что ушло провайдеру (AnalyteSearchQueryBuilder/
    /// medication.search-query.*) — единственное место в системе, где это сохраняется; не
    /// шифруется (см. class doc — не персональные данные, один и тот же текст независимо от
    /// того, кто и в какой семье искал название).</summary>
    public string QueryText { get; set; } = string.Empty;

    /// <summary>Путь провайдера ("v2/gen/search"/"res/v1/web/search") — для CacheHit пусто, запроса не было.</summary>
    public string? Endpoint { get; set; }

    public int? HttpStatus { get; set; }

    public int DurationMs { get; set; }

    public WebSearchCallOutcome Outcome { get; set; }

    public int SnippetCount { get; set; }

    /// <summary>URL реально использованных источников (после ответа провайдера, ДО фильтра по
    /// доверенным доменам — тот фильтр применяется процессором уже после записи лога) — JSON-массив
    /// строк, plaintext (не персональные данные). Null для CacheHit/Empty/ошибок.</summary>
    public string? ResultUrlsJson { get; set; }

    /// <summary>Усечённый текст исключения/причины отказа — для HttpError/Timeout/Rejected.</summary>
    public string? Error { get; set; }

    /// <summary>"Extraction"/"LabAnalyteEnrichment"/"MedicationEnrichment"/
    /// "VisitMedicationEnrichment" — зеркалит FamilyHub.Infrastructure.LmStudio.LlmJobKind СТРОКОЙ,
    /// не ссылкой на сам enum: FamilyHub.Domain не может зависеть от Infrastructure (см.
    /// FamilyHub.Domain.csproj — ноль ProjectReference, инвариант слоёв). Вместе с JobId — переход
    /// из строки лога в карточку задачи в /admin/pipeline.</summary>
    public string? JobKind { get; set; }

    public Guid? JobId { get; set; }
}
