namespace FamilyHub.Domain.Entities;

/// <summary>
/// Обезличенный общий справочник биоматериалов вне фиксированного <see cref="Enums.SpecimenType"/>
/// (пересборка enrich-пайплайна анализов) — LLM-провалидированные названия ("ликвор", "мокрота" и
/// т.п.), общие для всех пользователей, а не личный список каждого заново (см. GlobalSpecimenKbService,
/// который заменяет прежнюю чисто персональную логику UserSpecimenService). Пополняется и при ручном
/// вводе пользователя, и при извлечении документа, вернувшего биоматериал вне enum. Тот же принцип
/// изоляции, что у GlobalMedicationKb/GlobalLabAnalyteKb — никакого персонального контекста.
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
