namespace FamilyHub.Contracts.Events;

/// <summary>
/// Админ одобрил заявку на вступление в семью. Хендлер Notifications оповещает
/// остальных членов семьи о новом участнике.
/// </summary>
public record MemberApprovedEvent(Guid FamilyId, Guid UserId) : DomainEvent;
