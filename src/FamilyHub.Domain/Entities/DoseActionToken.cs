using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Одноразовый токен-«ключ» на действие над одним приёмом из push-уведомления (кнопки «Принял»,
/// «Отложить», «Пропустить»). Кнопка выполняется service worker'ом без сессии, поэтому авторизует
/// её именно токен: он привязан к приёму и получателю, живёт до конца срока приёма плюс сутки, а в
/// БД хранится только SHA-256 (ADR-0015; тот же приём, что у токенов публичной ссылки отчёта врачу).
/// </summary>
public class DoseActionToken
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 токена в hex; сам токен есть только в зашифрованном push-payload.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid DoseId { get; set; }

    /// <summary>От чьего имени выполняется действие (кому пришло напоминание).</summary>
    public Guid RecipientUserId { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DoseAction? UsedAction { get; set; }

    public DateTime CreatedAt { get; set; }
}
