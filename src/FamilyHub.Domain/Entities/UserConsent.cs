using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Факт принятия пользователем конкретной версии согласия на обработку ПДн (задача 2.3).
/// Намеренно БЕЗ FK на Users: строка — юридическое доказательство и переживает
/// удаление аккаунта (право на забвение не стирает факт того, что согласие было).
/// </summary>
public class UserConsent
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public ConsentKind Kind { get; set; }

    /// <summary>Версия текста согласия (Consents:CurrentVersion на момент принятия).</summary>
    public string Version { get; set; } = string.Empty;

    public DateTime AcceptedAt { get; set; }
}
