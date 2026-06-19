using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Оповещение конкретному пользователю (этап 3 п.10 брифа). В отличие от семейных ресурсов
/// (Medication, Birthday и т.п.) доступ — не по роли в семье, а строго по UserId-получателю:
/// пользователь видит и может прочитать только свои собственные оповещения.
/// </summary>
public class Notification
{
    public Guid Id { get; set; }

    /// <summary>Получатель — конкретный член семьи, а не семья целиком.</summary>
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    public NotificationType Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Id лекарства/дня рождения, по которому сформировано оповещение.</summary>
    public Guid RelatedEntityId { get; set; }

    /// <summary>
    /// Ключ идемпотентности повторных прогонов джобы (UNIQUE), например
    /// "med-exp:{medId}:{userId}" или "bday:{bdayId}:{userId}:{year}".
    /// </summary>
    public string DedupKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Момент успешной доставки через INotificationSender (null — пока не отправлено).</summary>
    public DateTime? SentAt { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAt { get; set; }
}
