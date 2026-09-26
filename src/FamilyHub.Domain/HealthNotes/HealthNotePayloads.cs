namespace FamilyHub.Domain.HealthNotes;

// Типизированные payload'ы записей дневника (сериализуются в HealthNote.DataJson).
// Живут в Domain, а не в модуле Medical: правила валидации и формы данных понадобятся любому
// клиенту (мобильному в том числе) без зависимости от веб-стека.

/// <summary>Симптом. Severity 1–10; Areas — ключи из <see cref="HealthNoteRules.BodyAreas"/>;
/// Detail — уточнение локализации свободным текстом.</summary>
public record SymptomData(int Severity, IReadOnlyList<string>? Areas, string? Detail);

/// <summary>Домашний замер. Code — ключ из <see cref="HealthMetricCatalog"/>; Value2 — нижнее
/// давление (только у составных замеров).</summary>
public record MetricData(string Code, decimal Value, decimal? Value2);

/// <summary>Самочувствие: Score 1 (ужасно) … 5 (отлично); Factors — ключи из
/// <see cref="HealthNoteRules.WellbeingFactors"/>.</summary>
public record WellbeingData(int Score, IReadOnlyList<string>? Factors);

/// <summary>Приём лекарства: название лежит в HealthNote.Title, здесь только доза («1 таблетка»).</summary>
public record MedicationIntakeData(string? Dose);

/// <summary>Сон. BedTime/WakeTime — моменты в UTC, Quality 1 (плохо) … 3 (хорошо).
/// Длительность не хранится — считается (<see cref="HealthNoteRules.SleepMinutes"/>).</summary>
public record SleepData(DateTime BedTime, DateTime WakeTime, int Quality);
