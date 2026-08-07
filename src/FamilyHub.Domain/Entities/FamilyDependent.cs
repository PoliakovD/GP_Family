namespace FamilyHub.Domain.Entities;

/// <summary>
/// Подопечный без своего User — ребёнок, питомец или пожилой родственник. Семейный ресурс
/// (как Medkit/Birthday): видим и управляем любым активным членом семьи (Member — создание и
/// правка, Admin — удаление). Не заводим фейковый User с синтетическим email — этот профиль
/// существует только как запись в семье, без своего аккаунта и входа.
/// </summary>
public class FamilyDependent : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    [Encrypted]
    public string Name { get; set; } = string.Empty;

    public DateOnly? BirthDate { get; set; }

    public bool IsPet { get; set; }

    /// <summary>Вид животного (кот, собака и т.д.) — заполняется только если IsPet == true,
    /// сервис принудительно очищает поле при IsPet == false.</summary>
    public string? PetSpecies { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
