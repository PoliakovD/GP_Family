namespace FamilyHub.Domain.Entities;

/// <summary>
/// Биоматериал, которого нет в фиксированном <see cref="Enums.SpecimenType"/> (UX-редизайн) —
/// пользователь может завести свой ("ликвор", "мокрота"), провалидированный один раз локальной
/// LLM (см. UserSpecimenService), дальше он живёт как обычная запись пользовательского
/// справочника и предлагается автоподсказкой. Всё plaintext, как AnalyteKey/Flag на
/// <see cref="LabIndicator"/> — участвует в ключе группировки/тренда, шифрование убило бы SQL-
/// индекс по нему (см. LabIndicator.SpecimenCustomId).
/// </summary>
public class UserSpecimen
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    /// <summary>Ключ дедупликации — тот же LabAnalyteNormalizer, что и у показателей (ё→е,
    /// гомоглифы, пунктуация); уникален в пределах владельца.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Как ввёл пользователь (или как поправила модель при валидации, например
    /// "ликвор" → "Ликвор (СМЖ)") — для отображения.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
