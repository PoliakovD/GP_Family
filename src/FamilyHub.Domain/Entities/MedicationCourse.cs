using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Курс приёма лекарства. Ведётся для одного человека: либо самого пользователя
/// (<see cref="SubjectUserId"/>), либо подопечного без аккаунта (<see cref="FamilyDependentId"/>) —
/// ровно одно из двух. Взрослый член семьи с аккаунтом ведёт свой курс сам, а остальные видят его
/// только как наблюдатели (<see cref="MedicationWatcher"/>). Аптечка семейная, поэтому связь с ней
/// (<see cref="MedicationId"/>) — только для списания остатка; запись в дневник пишется лишь для
/// собственных курсов (дневник строго личный).
///
/// Расписание хранится целиком в <see cref="ScheduleJson"/>, а будущие приёмы не материализуются:
/// строка <see cref="MedicationDose"/> появляется, когда приём наступил или его отметили. Поэтому
/// правка расписания меняет только будущее (<see cref="EffectiveFromUtc"/>), а история остаётся.
/// Название препарата и заметки — [Encrypted] (ADR-0002), значит SQL по ним не фильтрует.
/// </summary>
public class MedicationCourse
{
    public Guid Id { get; set; }

    /// <summary>Пользователь, для которого ведётся курс (свой курс). Без FK на User — чистится
    /// явно в AccountService, как HealthNote.</summary>
    public Guid? SubjectUserId { get; set; }

    /// <summary>Подопечный, для которого ведётся курс. FK с CASCADE: курс не переживает подопечного.</summary>
    public Guid? FamilyDependentId { get; set; }

    /// <summary>Семья подопечного (для курса подопечного — обязательна) или семья привязанной аптечки.</summary>
    public Guid? FamilyId { get; set; }

    public Guid CreatedByUserId { get; set; }

    [Encrypted]
    public string DrugName { get; set; } = string.Empty;

    [Encrypted]
    public string? Notes { get; set; }

    /// <summary>Снимок строки назначения, из которой создан курс («по 1 таб. 2 раза в день»).</summary>
    [Encrypted]
    public string? PrescriptionText { get; set; }

    /// <summary>Расписание — JSON <c>DoseSchedule</c> (см. MedicationCourses/DoseSchedule).</summary>
    public string ScheduleJson { get; set; } = "{}";

    public FoodRelation Food { get; set; }

    public DoseUnit DoseUnit { get; set; }

    /// <summary>Первый день курса — локальная дата в <see cref="TimeZoneId"/>.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Последний день курса включительно; null — «постоянно».</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>IANA-часовой пояс, в котором заданы времена приёма.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public MedicationCourseStatus Status { get; set; }

    /// <summary>Приёмы раньше этого момента не разворачиваются: правка расписания, возобновление и
    /// смена часового пояса не должны задним числом создавать «пропущенные» приёмы.</summary>
    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? PausedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Назначение, из которого создан курс (MedicalRecord, вид «визит»). Без FK: запись
    /// могут удалить, карточка курса должна это пережить.</summary>
    public Guid? SourceMedicalRecordId { get; set; }

    public int? SourcePrescriptionIndex { get; set; }

    /// <summary>Препарат в семейной аптечке, из которого списывается остаток. FK с SET NULL.</summary>
    public Guid? MedicationId { get; set; }

    public bool WriteOffEnabled { get; set; }

    /// <summary>Повторить напоминание через N минут (5/15/30); null — без повтора.</summary>
    public int? RepeatAfterMinutes { get; set; }

    /// <summary>Через сколько минут без отметки приём считается пропущенным.</summary>
    public int MissedAfterMinutes { get; set; } = 120;

    /// <summary>Предупредить, когда остатка в аптечке хватит на столько дней или меньше.</summary>
    public int LowStockDays { get; set; } = 5;

    /// <summary>Когда отправили предупреждение «заканчивается»; сбрасывается, когда запас пополнили.</summary>
    public DateTime? LowStockNotifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
