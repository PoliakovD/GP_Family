namespace FamilyHub.Domain.Entities;

/// <summary>
/// Обезличенный общий справочник ИСТОЧНИКОВ показателя (пересборка enrich-пайплайна анализов) —
/// не только биоматериал ("кровь", "моча"), но и любой другой источник, из которого получен
/// показатель ("ЭКГ", "УЗИ брюшной полости") — единственная таблица на оба рода понятия, никакого
/// фиксированного enum/switch в коде не осталось. LLM-нормализованные названия, общие для всех
/// пользователей, а не личный список каждого заново (см. GlobalSpecimenKbService и SpecimenResolver).
/// Пополняется и при ручном вводе пользователя (UserSpecimenService), и при извлечении документа
/// (SpecimenResolver). Содержит один служебный сентинел-row — см. SpecimenContextIds.Unresolved.
/// Тот же принцип изоляции, что у GlobalMedicationKb/GlobalLabAnalyteKb — никакого персонального
/// контекста.
/// </summary>
public class GlobalSpecimenKb
{
    public Guid Id { get; set; }

    /// <summary>Нормализованное название (см. LabAnalyteNormalizer.Normalize) — ключ дедупликации.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>"llm" (провалидировано при ручном вводе/извлечении документа) — на будущее место
    /// под другие источники, тот же формат поля, что у GlobalLabAnalyteKb.Source.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
