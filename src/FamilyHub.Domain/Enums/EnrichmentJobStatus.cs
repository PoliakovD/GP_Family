namespace FamilyHub.Domain.Enums;

/// <summary>Статус конвейера обогащения справочника препаратов (этап 4).</summary>
public enum EnrichmentJobStatus
{
    /// <summary>Поставлена в очередь Hangfire, ожидает обработки.</summary>
    Pending = 0,

    /// <summary>Обрабатывается процессором прямо сейчас.</summary>
    Running = 1,

    /// <summary>Справочник пополнен (или уже содержал препарат на момент повторной проверки).</summary>
    Completed = 2,

    /// <summary>Не удалось найти доверенные источники или пройти антигаллюцинационный гейт — не ретраится.</summary>
    Failed = 3,

    /// <summary>Пропущена из-за исчерпанной месячной квоты внешнего поиска — не ретраится.</summary>
    Skipped = 4,
}
