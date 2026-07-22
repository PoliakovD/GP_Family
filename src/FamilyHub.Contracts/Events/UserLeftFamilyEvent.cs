namespace FamilyHub.Contracts.Events;

/// <summary>
/// Пользователь покинул семью (сам вышел или выгнан админом). Хендлеры: Medical отзывает
/// FamilyMedicalShare ушедшего для этой семьи; Notifications оповещает админов семьи.
/// </summary>
public record UserLeftFamilyEvent(Guid FamilyId, Guid UserId) : DomainEvent;
