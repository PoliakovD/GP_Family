using System.Text.Json;
using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.HealthNotes;

/// <summary>Содержимое записи дневника без служебных полей: вид + название + текст + ровно один
/// payload, соответствующий виду (остальные — null).</summary>
public record HealthNoteContent(
    HealthNoteKind Kind,
    string? Title,
    string? Text,
    SymptomData? Symptom = null,
    MetricData? Metric = null,
    WellbeingData? Wellbeing = null,
    MedicationIntakeData? Intake = null,
    SleepData? Sleep = null);

/// <summary>Правила записи дневника: допустимые справочные ключи, валидация и (де)сериализация
/// payload'а. Единое место для всех клиентов — см. HealthNotePayloads.</summary>
public static class HealthNoteRules
{
    public const int MaxTitleLength = 200;
    public const int MaxTextLength = 4000;

    public static readonly IReadOnlySet<string> BodyAreas = new HashSet<string>(StringComparer.Ordinal)
    {
        "head", "chest", "abdomen", "back", "joints", "throat",
    };

    /// <summary>Подписи локализации по-русски — для документов (PDF отчёта); в интерфейсе подписи свои.</summary>
    public static readonly IReadOnlyDictionary<string, string> BodyAreaLabels = new Dictionary<string, string>
    {
        ["head"] = "голова", ["chest"] = "грудь", ["abdomen"] = "живот",
        ["back"] = "спина", ["joints"] = "суставы", ["throat"] = "горло",
    };

    public static readonly IReadOnlySet<string> WellbeingFactors = new HashSet<string>(StringComparer.Ordinal)
    {
        "rested", "stress", "sport", "weather",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static int SleepMinutes(SleepData sleep) => (int)(sleep.WakeTime - sleep.BedTime).TotalMinutes;

    /// <summary>null — содержимое корректно; иначе сообщение об ошибке (для 400).</summary>
    public static string? Validate(HealthNoteContent c)
    {
        if (!Enum.IsDefined(c.Kind)) return "Неизвестный вид записи.";
        if (c.Title is { Length: > MaxTitleLength }) return "Слишком длинное название.";
        if (c.Text is { Length: > MaxTextLength }) return "Слишком длинный текст.";

        // Ровно один payload — тот, что соответствует виду; лишние отвергаем, чтобы не копить мусор.
        var provided = new[] { c.Symptom is not null, c.Metric is not null, c.Wellbeing is not null,
            c.Intake is not null, c.Sleep is not null }.Count(x => x);
        var expectsPayload = c.Kind != HealthNoteKind.Note;
        if (provided > 1 || (!expectsPayload && provided > 0)) return "Лишние данные для этого вида записи.";

        return c.Kind switch
        {
            HealthNoteKind.Symptom => ValidateSymptom(c),
            HealthNoteKind.Metric => ValidateMetric(c.Metric),
            HealthNoteKind.Wellbeing => ValidateWellbeing(c.Wellbeing),
            HealthNoteKind.MedicationIntake => ValidateIntake(c),
            HealthNoteKind.Sleep => ValidateSleep(c.Sleep),
            HealthNoteKind.Note => string.IsNullOrWhiteSpace(c.Text) ? "Введите текст заметки." : null,
            _ => "Неизвестный вид записи.",
        };
    }

    private static string? ValidateSymptom(HealthNoteContent c)
    {
        if (string.IsNullOrWhiteSpace(c.Title)) return "Укажите, что беспокоит.";
        if (c.Symptom is null) return "Не указана интенсивность симптома.";
        if (c.Symptom.Severity is < 1 or > 10) return "Интенсивность — от 1 до 10.";
        if (c.Symptom.Detail is { Length: > MaxTitleLength }) return "Слишком длинное уточнение.";
        if (c.Symptom.Areas is { } areas && (areas.Count > BodyAreas.Count || areas.Any(a => !BodyAreas.Contains(a))))
            return "Неизвестная локализация.";
        return null;
    }

    private static string? ValidateMetric(MetricData? m)
    {
        if (m is null) return "Не указан замер.";
        var def = HealthMetricCatalog.Find(m.Code);
        if (def is null) return "Неизвестный вид замера.";
        if (m.Value < def.Min || m.Value > def.Max) return $"{def.Name}: значение вне допустимого диапазона.";
        if (def.HasSecondValue)
        {
            if (m.Value2 is not { } v2) return $"{def.Name}: укажите второе значение.";
            if (v2 < def.Min2 || v2 > def.Max2 || v2 >= m.Value) return $"{def.Name}: второе значение некорректно.";
        }
        else if (m.Value2 is not null)
        {
            return $"{def.Name}: второе значение не используется.";
        }
        return null;
    }

    private static string? ValidateWellbeing(WellbeingData? w)
    {
        if (w is null) return "Не указано самочувствие.";
        if (w.Score is < 1 or > 5) return "Самочувствие — от 1 до 5.";
        if (w.Factors is { } f && (f.Count > WellbeingFactors.Count || f.Any(x => !WellbeingFactors.Contains(x))))
            return "Неизвестный фактор самочувствия.";
        return null;
    }

    private static string? ValidateIntake(HealthNoteContent c)
    {
        if (string.IsNullOrWhiteSpace(c.Title)) return "Укажите, что приняли.";
        if (c.Intake?.Dose is { Length: > MaxTitleLength }) return "Слишком длинное описание дозы.";
        return null;
    }

    private static string? ValidateSleep(SleepData? s)
    {
        if (s is null) return "Не указан сон.";
        if (s.Quality is < 1 or > 3) return "Качество сна — от 1 до 3.";
        var minutes = SleepMinutes(s);
        if (minutes <= 0 || minutes > 24 * 60) return "Время сна указано неверно.";
        return null;
    }

    /// <summary>Payload записи → JSON для HealthNote.DataJson (null для Note).</summary>
    public static string? SerializePayload(HealthNoteContent c) => c.Kind switch
    {
        HealthNoteKind.Symptom => JsonSerializer.Serialize(c.Symptom, Json),
        HealthNoteKind.Metric => JsonSerializer.Serialize(c.Metric, Json),
        HealthNoteKind.Wellbeing => JsonSerializer.Serialize(c.Wellbeing, Json),
        HealthNoteKind.MedicationIntake => c.Intake is null ? null : JsonSerializer.Serialize(c.Intake, Json),
        HealthNoteKind.Sleep => JsonSerializer.Serialize(c.Sleep, Json),
        _ => null,
    };

    /// <summary>Обратное преобразование: строка из БД → содержимое. Битый JSON не роняет чтение
    /// ленты — payload остаётся null, запись всё равно отображается по Title/Text.</summary>
    public static HealthNoteContent ParseContent(HealthNoteKind kind, string? title, string? text, string? dataJson)
    {
        var content = new HealthNoteContent(kind, title, text);
        if (string.IsNullOrEmpty(dataJson)) return content;
        try
        {
            return kind switch
            {
                HealthNoteKind.Symptom => content with { Symptom = JsonSerializer.Deserialize<SymptomData>(dataJson, Json) },
                HealthNoteKind.Metric => content with { Metric = JsonSerializer.Deserialize<MetricData>(dataJson, Json) },
                HealthNoteKind.Wellbeing => content with { Wellbeing = JsonSerializer.Deserialize<WellbeingData>(dataJson, Json) },
                HealthNoteKind.MedicationIntake => content with { Intake = JsonSerializer.Deserialize<MedicationIntakeData>(dataJson, Json) },
                HealthNoteKind.Sleep => content with { Sleep = JsonSerializer.Deserialize<SleepData>(dataJson, Json) },
                _ => content,
            };
        }
        catch (JsonException)
        {
            return content;
        }
    }
}
