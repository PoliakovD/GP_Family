namespace FamilyHub.Api.Features.Dependents;

public record FamilyDependentDto(
    Guid Id,
    Guid FamilyId,
    string Name,
    DateOnly? BirthDate,
    bool IsPet,
    string? PetSpecies,
    Guid CreatedByUserId,
    DateTime CreatedAt);

public record CreateFamilyDependentRequest(string Name, DateOnly? BirthDate, bool IsPet, string? PetSpecies);

public record UpdateFamilyDependentRequest(string Name, DateOnly? BirthDate, bool IsPet, string? PetSpecies);

public enum FamilyDependentAccessResult { Success, Forbidden, NotFound }
