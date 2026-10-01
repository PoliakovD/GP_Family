namespace FamilyHub.Domain.Enums;

/// <summary>
/// Группы статусов задач обогащения, которые раньше повторялись в десятке запросов как цепочки
/// <c>Pending || Running || Deferred</c>. После ADR-0018 к «живым» добавились два статуса ожидания
/// админа — забыть их в одном из мест значит нарушить частичный уникальный индекс (Status IN
/// (0,1,5,6,7)) или предложить пользователю повторный запрос, который упрётся в дедуп.
/// Массивы (а не методы) — чтобы EF транслировал <c>Live.Contains(j.Status)</c> в SQL IN.
/// </summary>
public static class EnrichmentJobStatusSets
{
    /// <summary>Задача жива и занимает слот дедуп-индекса: Pending, Running, Deferred,
    /// AwaitingSearchApproval, AwaitingResultReview. Должно совпадать с HasFilter индексов в
    /// *EnrichmentJobConfiguration.</summary>
    public static readonly EnrichmentJobStatus[] Live =
    [
        EnrichmentJobStatus.Pending,
        EnrichmentJobStatus.Running,
        EnrichmentJobStatus.Deferred,
        EnrichmentJobStatus.AwaitingSearchApproval,
        EnrichmentJobStatus.AwaitingResultReview,
    ];

    /// <summary>Ждёт ручного действия админа (ADR-0018) — НЕ запускается ни одним фоновым
    /// джобом, только одобрением из очереди «Одобрение».</summary>
    public static readonly EnrichmentJobStatus[] AwaitingAdmin =
    [
        EnrichmentJobStatus.AwaitingSearchApproval,
        EnrichmentJobStatus.AwaitingResultReview,
    ];

    public static bool IsAwaitingAdmin(this EnrichmentJobStatus status) =>
        status is EnrichmentJobStatus.AwaitingSearchApproval or EnrichmentJobStatus.AwaitingResultReview;
}
