namespace FamilyHub.Domain.Entities;

/// <summary>
/// Наблюдатель за курсами человека: кого уведомлять о его приёмах и пропусках. Субъект — ровно одно
/// из <see cref="SubjectUserId"/> (взрослый с аккаунтом сам выбирает, «кто узнает о моих пропусках») и
/// <see cref="FamilyDependentId"/> (подопечный: следить может любой член семьи, создатель курса
/// добавляется автоматически). Наблюдатель за взрослым видит его курсы только на чтение.
/// </summary>
public class MedicationWatcher
{
    public Guid Id { get; set; }

    /// <summary>Субъект-пользователь. Без FK на User — чистится явно в AccountService.</summary>
    public Guid? SubjectUserId { get; set; }

    /// <summary>Субъект-подопечный. FK с CASCADE.</summary>
    public Guid? FamilyDependentId { get; set; }

    public Guid WatcherUserId { get; set; }

    /// <summary>Получать напоминания о каждом приёме (для подопечных — те, кто даёт лекарство).</summary>
    public bool ReceiveReminders { get; set; }

    /// <summary>Получать уведомление, если приём не отмечен вовремя.</summary>
    public bool NotifyMissed { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
