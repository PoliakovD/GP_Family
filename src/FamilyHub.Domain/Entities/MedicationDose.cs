using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Конкретный приём курса. Строка создаётся, когда приём наступил (фоновая задача) или когда его
/// отметили в приложении; будущие приёмы вычисляются из расписания на лету. Так история и
/// статистика — это просто строки, а бессрочный курс ничего не хранит заранее.
/// </summary>
public class MedicationDose
{
    public Guid Id { get; set; }

    public Guid CourseId { get; set; }

    /// <summary>Плановое время (UTC). null — приём «по необходимости», у него нет плана.</summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>Доза в единицах курса (1 таб., 2,5 мл).</summary>
    public decimal Units { get; set; }

    public DoseStatus Status { get; set; }

    /// <summary>Когда реально приняли (для «по необходимости» — когда отметили).</summary>
    public DateTime? TakenAt { get; set; }

    public DateTime? SnoozedUntil { get; set; }

    /// <summary>Сколько раз откладывали — часть ключа дедупликации напоминания после отсрочки.</summary>
    public int SnoozeCount { get; set; }

    /// <summary>Когда отправили первое напоминание.</summary>
    public DateTime? RemindedAt { get; set; }

    /// <summary>Когда отправили повтор «если не отмечено»; повтор один.</summary>
    public DateTime? RepeatSentAt { get; set; }

    /// <summary>Кто отметил: владелец курса или член семьи, следящий за подопечным.</summary>
    public Guid? ActedByUserId { get; set; }

    /// <summary>Запись дневника, созданная отметкой (только для собственных курсов).</summary>
    public Guid? HealthNoteId { get; set; }

    /// <summary>Из какого препарата и сколько списано из аптечки — чтобы отмена вернула ровно столько же.</summary>
    public Guid? WriteOffMedicationId { get; set; }

    public decimal? WriteOffUnits { get; set; }

    public DateTime CreatedAt { get; set; }
}
