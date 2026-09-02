namespace FamilyHub.Domain.Enums;

public enum KbRebuildStatus
{
    /// <summary>Идёт (или ждёт своей очереди в Hangfire) прямо сейчас.</summary>
    Running = 0,

    /// <summary>Все четыре этапа (см. class doc LabAnalyteKbRebuildJob) пройдены успешно.</summary>
    Completed = 1,

    /// <summary>Упал на необработанном исключении после исчерпания ретраев Hangfire — см. LastError.</summary>
    Failed = 2,
}
