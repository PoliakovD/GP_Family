namespace FamilyHub.Domain.Enums;

/// <summary>Режим расписания курса приёма. Значения — часть контракта с фронтом (числа в JSON).</summary>
public enum DoseScheduleMode
{
    /// <summary>«N раз в день»: явный список времён, у каждого своя доза.</summary>
    TimesPerDay = 0,

    /// <summary>«Каждые N часов» от времени старта; N делит сутки нацело, поэтому времена одинаковы каждый день.</summary>
    EveryNHours = 1,

    /// <summary>«По дням недели»: выбранные дни, в каждом — список времён.</summary>
    Weekdays = 2,

    /// <summary>«Через день / цикл»: X дней приёма, затем Y дней перерыва.</summary>
    Cycle = 3,

    /// <summary>«По необходимости»: без напоминаний, только лимит приёмов в сутки.</summary>
    AsNeeded = 4,
}
