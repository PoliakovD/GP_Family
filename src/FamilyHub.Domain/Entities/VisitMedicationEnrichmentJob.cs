using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Задача обогащения справочника медикаментов (kb.global_medications_kb) для препарата,
/// упомянутого в распознанном заключении врача (UX-редизайн) — зеркало
/// <see cref="LabAnalyteEnrichmentJob"/>, не <see cref="MedicationEnrichmentJob"/>: у визита к
/// врачу нет FamilyId (MedicalRecord — персональный ресурс, см. раздел 4.2 брифа), а конвейер
/// аптечки требует его для уведомления семьи. Отдельная таблица — не блок для существующего
/// семейного конвейера, дедуп-индекс по NormalizedName независим от него (тот же приём, что у
/// LabAnalyteEnrichmentJob против MedicationEnrichmentJob — разные справочники/контуры не должны
/// конкурировать за один индекс, здесь общий с MedicationEnrichmentJob СПРАВОЧНИК, но раздельные
/// ТАБЛИЦЫ ЗАДАЧ, поэтому дедуп по NormalizedName проверяется в сервисе против обеих сразу).
/// Наружу конвейер отправляет только нормализованное имя — см. VisitMedicationEnrichmentProcessor.
/// </summary>
public class VisitMedicationEnrichmentJob
{
    public Guid Id { get; set; }

    public string NormalizedName { get; set; } = string.Empty;

    public string SourceDisplayName { get; set; } = string.Empty;

    /// <summary>Запись, из-за которой создана задача — справочно, не FK.</summary>
    public Guid? MedicalRecordId { get; set; }

    public Guid RequestedByUserId { get; set; }

    public EnrichmentJobStatus Status { get; set; } = EnrichmentJobStatus.Pending;

    public int Attempts { get; set; }

    public string? Error { get; set; }

    public string? Provider { get; set; }

    public DateTime? ExternalSearchAt { get; set; }

    public Guid? KbId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
