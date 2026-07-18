namespace FamilyHub.Domain.Entities;

/// <summary>Медикамент внутри аптечки — семейный ресурс. Видна всем активным членам семьи, управляет админ.</summary>
public class Medication : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid MedkitId { get; set; }
    public Medkit Medkit { get; set; } = null!;

    /// <summary>Денормализовано из Medkit.FamilyId — используется джобой оповещений и проверками
    /// доступа без дополнительного join'а (инвариант 1: списки всегда фильтруются по FamilyId).</summary>
    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    /// <summary>Для оповещений о сроке годности (этап 3).</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>
    /// Всё остальное про медикамент — единым JSON (jsonb): инструкция, количество,
    /// производитель, дозировка, действующее вещество, и любые доп. поля, найденные при
    /// оцифровке по фото. Только Name/ExpiryDate вынесены отдельными колонками — по ним
    /// джоба оповещений строит SQL-фильтр (см. ReminderScanJob.ScanMedicationsAsync).
    /// Сериализуется/десериализуется на границе в MedicationService, здесь — сырой JSON.
    /// </summary>
    public string? DataJson { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
