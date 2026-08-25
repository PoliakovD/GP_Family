namespace FamilyHub.Domain.Entities;

/// <summary>
/// Обезличенный справочник лабораторных показателей (ветка medicalrecords, зеркало
/// <see cref="GlobalMedicationKb"/> — тот же принцип: НИКАКОГО персонального контекста, ключ —
/// нормализованное имя показателя, знание едино для всех. Инвариант охраняет
/// KbIsolationGuardTests наравне со справочником медикаментов.
/// </summary>
public class GlobalLabAnalyteKb
{
    public Guid Id { get; set; }

    /// <summary>Нормализованное имя (см. LabAnalyteNormalizer) — ключ дедупликации.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Обогащённые данные (jsonb): plainExplanation, whyMeasured, highMeans, lowMeans,
    /// refRanges[{sex, ageFrom, ageTo, low, high, unit}], loincCode, defaultUnit.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Источник знания — "brave: helix.ru, invitro.ru" и т.п. (см. BuildSourceLabel в
    /// MedicationEnrichmentProcessor, тот же формат).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Синонимы ("Hb", "HGB" для "гемоглобин") — Postgres text[], НЕ заведено в EF-модель
    /// (как у GlobalMedicationKb.Aliases) — читается/пишется только raw SQL.</summary>
    public string[] Aliases { get; set; } = [];

    public int PayloadVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
