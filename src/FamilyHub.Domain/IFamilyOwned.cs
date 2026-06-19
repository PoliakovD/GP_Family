namespace FamilyHub.Domain;

/// <summary>
/// Помечает сущность как принадлежащую семье (семейный ресурс — аптечка, ДР, будущие чат/события).
/// MedicalRecord этот интерфейс НЕ реализует: анализы принадлежат пользователю, не семье.
/// </summary>
public interface IFamilyOwned
{
    Guid FamilyId { get; }
}
