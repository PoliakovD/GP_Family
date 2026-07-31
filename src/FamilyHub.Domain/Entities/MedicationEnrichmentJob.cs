using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача конвейера обогащения справочника (этап 4): OCR/ручной ввод → нормализация имени →
/// промах в kb.global_medications_kb → веб-поиск → суммаризация локальным Qwen → запись в kb.
/// Живёт в схеме medical (не kb!) — у неё есть персональный контекст (кто попросил, в какой
/// семье), поэтому она не может лежать рядом с обезличенным справочником (см. KbIsolationGuardTests).
/// Дедуп на уровне БД: частичный уникальный индекс по NormalizedName среди Pending/Running —
/// один и тот же препарат, сохранённый одновременно в разных семьях, порождает один внешний запрос.
/// </summary>
public class MedicationEnrichmentJob
{
    public Guid Id { get; set; }

    /// <summary>Нормализованное название — ключ поиска в kb и ключ дедупликации задач.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Название препарата как ввёл/распознал пользователь — для читаемости статуса.</summary>
    public string SourceDisplayName { get; set; } = string.Empty;

    /// <summary>Медикамент, из-за сохранения которого создана задача (может исчезнуть — не FK, только справочно).</summary>
    public Guid? MedicationId { get; set; }

    /// <summary>Кто инициировал обогащение — персональный контекст, поэтому не в kb.</summary>
    public Guid RequestedByUserId { get; set; }

    public Guid FamilyId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>Провайдер внешнего поиска, фактически использованный (например, "Brave").</summary>
    public string? Provider { get; set; }

    /// <summary>Момент фактического внешнего запроса — база для подсчёта месячной квоты (Postgres, не in-memory).</summary>
    public DateTime? ExternalSearchAt { get; set; }

    /// <summary>Строка справочника, которой завершилась задача (справочно, не FK — kb не ссылается наружу и внутрь).</summary>
    public Guid? KbId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
