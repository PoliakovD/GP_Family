namespace FamilyHub.Modules.Birthdays.Birthdays;

/// <summary>
/// Источник записи (identity rework) — объявлен здесь же, а не переиспользует
/// FamilyHub.Contracts.Events.BirthdaySubjectKind: этот проект не ссылается на Contracts
/// (модуль намеренно легковесный, см. AddBirthdayModule/MapBirthdayModule), а внутренний
/// рефакторинг события напоминания не должен молча поменять формат HTTP-ответа списка.
/// Manual — редактируемая запись Birthday; Member/Dependent — производные из профиля User/
/// FamilyDependent, только для чтения (Create/Update/Delete ниже применимы только к Manual).
/// </summary>
public enum BirthdaySource { Manual, Member, Dependent }

public record BirthdayDto(Guid Id, Guid FamilyId, string PersonName, DateOnly Date, BirthdaySource Source);

public record CreateBirthdayRequest(string PersonName, DateOnly Date);

public record UpdateBirthdayRequest(string PersonName, DateOnly Date);

public enum BirthdayAccessResult { Success, Forbidden, NotFound }
