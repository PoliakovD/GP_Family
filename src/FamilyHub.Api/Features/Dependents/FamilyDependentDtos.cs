using FamilyHub.Domain.Enums;

namespace FamilyHub.Api.Features.Dependents;

// FirstName — имя человека или кличка питомца. LastName/MiddleName — только для людей, сервис
// зануляет их при IsPet == true (не доверяет фронту, тот же принцип, что уже применялся к
// PetSpecies). Gender обязателен для всех — используется ReminderScanJob для текста напоминания.
public record FamilyDependentDto(
    Guid Id,
    Guid FamilyId,
    string FirstName,
    string? LastName,
    string? MiddleName,
    Gender Gender,
    DateOnly? BirthDate,
    bool IsPet,
    string? PetSpecies,
    Guid CreatedByUserId,
    DateTime CreatedAt);

public record CreateFamilyDependentRequest(
    string FirstName, string? LastName, string? MiddleName, Gender Gender,
    DateOnly? BirthDate, bool IsPet, string? PetSpecies);

public record UpdateFamilyDependentRequest(
    string FirstName, string? LastName, string? MiddleName, Gender Gender,
    DateOnly? BirthDate, bool IsPet, string? PetSpecies);

public enum FamilyDependentAccessResult { Success, Forbidden, NotFound }
