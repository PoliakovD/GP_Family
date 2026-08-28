using FamilyHub.Domain.Entities;

namespace FamilyHub.Domain.ValueObjects;

/// <summary>
/// Общий предикат "лекарство просрочено или истекает в ближайшие N дней" — редизайн v2. Нужен
/// двум независимым потребителям в разных слоях: <c>FamilyHub.Infrastructure.Notifications.ReminderScanJob</c>
/// (пуш-уведомления) и <c>FamilyHub.Api.Features.Home.HomeSummaryService</c> (блок «Требует
/// внимания» на Главной). В Domain, а не в одном из модулей — Infrastructure не может ссылаться
/// на FamilyHub.Modules.Medical (обратная зависимость сломала бы граф проекта: Modules.* и так
/// уже зависят от Infrastructure), а Api зависит от обоих слоёв (см. patterns/backend.md,
/// "Разделяемая политика формата — в Domain, не в сервисе").
/// </summary>
public static class MedicationExpiryPolicy
{
    /// <summary>ExpiryDate задан и не позже today+warningDays (покрывает и уже просроченные —
    /// ExpiryDate меньше today, и приближающиеся). Различать просрочено/истекает — на вызывающей
    /// стороне сравнением с today, как и раньше делал ReminderScanJob.</summary>
    public static IQueryable<Medication> WhereExpiringOrExpired(
        this IQueryable<Medication> medications, DateOnly today, int warningDays)
    {
        var warningCutoff = today.AddDays(warningDays);
        return medications.Where(m => m.ExpiryDate != null && m.ExpiryDate <= warningCutoff);
    }
}
