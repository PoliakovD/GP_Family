namespace FamilyHub.Contracts.Events;

/// <summary>
/// Владелец открыл семье доступ к своим медицинским записям (FamilyMedicalShare создан).
/// Хендлер Notifications оповещает членов семьи о выданном доступе.
/// </summary>
public record MedicalRecordSharedEvent(Guid FamilyId, Guid OwnerUserId) : DomainEvent;
