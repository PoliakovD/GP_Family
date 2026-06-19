namespace FamilyHub.Modules.Birthdays.Birthdays;

public record BirthdayDto(Guid Id, Guid FamilyId, string PersonName, DateOnly Date);

public record CreateBirthdayRequest(string PersonName, DateOnly Date);

public record UpdateBirthdayRequest(string PersonName, DateOnly Date);

public enum BirthdayAccessResult { Success, Forbidden, NotFound }
