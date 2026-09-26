namespace FamilyHub.Domain.HealthNotes;

/// <summary>Описание одного домашнего замера. Диапазоны — границы правдоподобного ввода (защита
/// от опечаток вроде «1280»), а не медицинская норма: дневник не ставит диагнозов.</summary>
public record HealthMetricDefinition(
    string Code,
    string Name,
    string Unit,
    decimal Min,
    decimal Max,
    bool HasSecondValue,
    decimal? Min2 = null,
    decimal? Max2 = null);

/// <summary>Справочник замеров дневника. Коды стабильны — они хранятся в записях и образуют мост к
/// внешним health-форматам, поэтому переименовывать нельзя.</summary>
public static class HealthMetricCatalog
{
    public static readonly IReadOnlyList<HealthMetricDefinition> All =
    [
        new("blood_pressure", "Давление", "мм рт. ст.", 50, 300, true, 30, 200),
        new("pulse", "Пульс", "уд/мин", 20, 250, false),
        new("weight", "Вес", "кг", 1, 500, false),
        new("glucose", "Глюкоза", "ммоль/л", 0.5m, 50, false),
        new("temperature", "Температура", "°C", 30, 45, false),
        new("spo2", "SpO₂", "%", 50, 100, false),
    ];

    public static HealthMetricDefinition? Find(string? code) =>
        All.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.Ordinal));
}
