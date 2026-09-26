namespace FamilyHub.Domain.Enums;

/// <summary>Тип родительской сущности, к которой относится вложение.</summary>
public enum FileOwnerType
{
    MedicalRecord = 0,
    Medication = 1,

    /// <summary>PDF отчёта для врача (DoctorReport) — блоб шифруется и ротируется общим механизмом
    /// вложений, но видимости через AttachmentService у него нет: доступ только через владельца
    /// отчёта или по токену ссылки (DoctorReportService).</summary>
    DoctorReport = 2,
}
