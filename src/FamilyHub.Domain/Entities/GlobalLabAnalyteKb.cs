namespace FamilyHub.Domain.Entities;

/// <summary>
/// Обезличенный справочник лабораторных показателей (ветка medicalrecords, зеркало
/// <see cref="GlobalMedicationKb"/> — тот же принцип: НИКАКОГО персонального контекста, знание
/// едино для всех. Ключ дедупликации — пара (NormalizedName, SpecimenKbId), не одно имя
/// (пересборка enrich-пайплайна): один и тот же показатель имеет разные нормы в разных источниках
/// ("белок" в крови и в моче) и не может делить одну запись. Инвариант изоляции охраняет
/// KbIsolationGuardTests наравне со справочником медикаментов.
/// </summary>
public class GlobalLabAnalyteKb
{
    public Guid Id { get; set; }

    /// <summary>Нормализованное имя (см. LabAnalyteNormalizer) — часть ключа дедупликации вместе с SpecimenKbId.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Источник показателя — ссылка (не FK, GlobalSpecimenKb в той же схеме kb, но
    /// самостоятельная таблица) на <see cref="GlobalSpecimenKb"/> — вторая половина ключа
    /// дедупликации. Записи со значением <see cref="SpecimenContextIds.Unresolved"/> служат
    /// обобщённым фолбэком (см. LabAnalyteKbLookupService), когда специфичной по источнику записи
    /// ещё нет; заводятся только вручную из админки (обычный enrich-конвейер такие не создаёт —
    /// см. жёсткий гейт в LabAnalyteEnrichmentRequestService).</summary>
    public Guid SpecimenKbId { get; set; } = SpecimenContextIds.Unresolved;

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

    /// <summary>Поля, защищённые от перезаписи автообогащением после ручной правки из админки
    /// (§3 плана) — подмножество {"displayName", "payload", "aliases"}. Postgres text[], та же
    /// причина вне EF-модели, что у Aliases. Пусто — переобогащение обновляет всё как раньше.</summary>
    public string[] LockedFields { get; set; } = [];

    public int PayloadVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
