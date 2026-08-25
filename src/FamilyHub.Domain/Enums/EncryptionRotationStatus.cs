namespace FamilyHub.Domain.Enums;

/// <summary>Статус фонового прогона перешифровки при ротации ключа (ADR-0009).</summary>
public enum EncryptionRotationStatus
{
    /// <summary>Идёт (или ждёт своей очереди в Hangfire) прямо сейчас.</summary>
    Running = 0,

    /// <summary>Все поля и блобы, существовавшие на момент запуска, перешифрованы активным ключом.</summary>
    Completed = 1,

    /// <summary>Упал на необработанном исключении — см. LastError. Hangfire уже исчерпал свои ретраи.</summary>
    Failed = 2,

    /// <summary>Остановлен по запросу администратора (CancelRequested) до завершения.</summary>
    Cancelled = 3,
}
