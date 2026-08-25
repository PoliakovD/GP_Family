using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Подопечный без своего User — ребёнок, питомец или пожилой родственник. Семейный ресурс
/// (как Medkit/Birthday): видим и управляем любым активным членом семьи (Member — создание и
/// правка, Admin — удаление). Не заводим фейковый User с синтетическим email — этот профиль
/// существует только как запись в семье, без своего аккаунта и входа.
///
/// ФИО (identity rework): для человека (IsPet == false) — полное ФИО, LastName/MiddleName
/// обязательны только сервисом (не БД — см. FamilyDependentService), для питомца (IsPet == true)
/// FirstName — кличка, LastName/MiddleName сервис принудительно зануляет (тот же приём, что
/// уже применялся к PetSpecies). Gender обязателен для всех — используется в напоминаниях о ДР.
/// </summary>
public class FamilyDependent : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    /// <summary>Имя человека или кличка питомца.</summary>
    [Encrypted]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>Только для людей (IsPet == false) — сервис зануляет при IsPet == true.</summary>
    [Encrypted]
    public string? LastName { get; set; }

    /// <summary>Только для людей (IsPet == false), необязательно и для них — сервис зануляет при IsPet == true.</summary>
    [Encrypted]
    public string? MiddleName { get; set; }

    public Gender Gender { get; set; }

    public DateOnly? BirthDate { get; set; }

    public bool IsPet { get; set; }

    /// <summary>Вид животного (кот, собака и т.д.) — заполняется только если IsPet == true,
    /// сервис принудительно очищает поле при IsPet == false.</summary>
    public string? PetSpecies { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
