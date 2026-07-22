namespace FamilyHub.Domain.Entities;

/// <summary>День рождения члена семьи — семейный ресурс.</summary>
public class Birthday : IFamilyOwned
{
    public Guid Id { get; set; }

    public Guid FamilyId { get; set; }
    public Family Family { get; set; } = null!;

    [Encrypted]
    public string PersonName { get; set; } = string.Empty;

    /// <summary>
    /// Не шифруется: по дате SQL-фильтрует ReminderScanJob; дата без имени
    /// (оно зашифровано) — низкий риск, зафиксировано в ADR-0002.
    /// </summary>
    public DateOnly Date { get; set; }
}
