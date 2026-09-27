using FamilyHub.Domain.Vaccinations;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Один зафиксированный факт по серии прививок (см. <see cref="Vaccinations.VaccinationFact"/> — то же,
/// в форме EF-сущности). Ведётся для одного человека: либо самого пользователя
/// (<see cref="SubjectUserId"/>), либо подопечного без аккаунта (<see cref="FamilyDependentId"/>) —
/// ровно одно из двух, как у <see cref="MedicationCourse"/>. Доступ — тот же уровень (свой/подопечный
/// семьи/наблюдатель за взрослым, только чтение) — см. Modules.Medical.Vaccinations.VaccinationAccess.
///
/// Прививка либо из календаря (<see cref="SeriesCode"/>+<see cref="DoseIndex"/> ссылаются на
/// <see cref="VaccineCatalog"/>), либо введена вручную (<see cref="CustomName"/>) — сам график
/// строится на лету калькулятором, здесь хранятся только факты.
/// </summary>
public class Vaccination
{
    public Guid Id { get; set; }

    /// <summary>Пользователь, для которого ведётся запись (своя прививка). Без FK на User —
    /// чистится явно в AccountService, как SubjectUserId у MedicationCourse.</summary>
    public Guid? SubjectUserId { get; set; }

    /// <summary>Подопечный, для которого ведётся запись. FK с CASCADE: запись не переживает подопечного.</summary>
    public Guid? FamilyDependentId { get; set; }

    /// <summary>Семья подопечного — обязательна для записи подопечного (доступ считается по ней), null для своей.</summary>
    public Guid? FamilyId { get; set; }

    public Guid CreatedByUserId { get; set; }

    /// <summary>Код серии из <see cref="VaccineCatalog"/>; null — прививка не из календаря (см. <see cref="CustomName"/>).</summary>
    public string? SeriesCode { get; set; }

    /// <summary>Индекс дозы в <c>VaccineSeries.Doses</c>, либо <c>Doses.Count + k</c> для k-го повтора
    /// после них (см. <see cref="VaccinationScheduleCalculator"/>). Null для прививки не из календаря
    /// и для факта «болел(а)», отмеченного не на конкретную дозу.</summary>
    public int? DoseIndex { get; set; }

    /// <summary>Название прививки не из календаря («от чего или вакцина», введено вручную).</summary>
    [Encrypted]
    public string? CustomName { get; set; }

    /// <summary>Конкретная вакцина/производитель — необязательно, для дозы из календаря и вручную введённой.</summary>
    [Encrypted]
    public string? VaccineName { get; set; }

    public VaccinationKind Kind { get; set; }

    public DateOnly? Date { get; set; }

    public VaccinationDatePrecision? DatePrecision { get; set; }

    /// <summary>Общий скан сертификата (см. <see cref="VaccinationCertificate"/>), из распознавания
    /// которого создана эта запись — несколько записей могут указывать на один и тот же скан
    /// (файл виден у каждой найденной прививки без копирования блоба). Без FK: сертификат может
    /// быть удалён отдельно, запись должна это пережить.</summary>
    public Guid? CertificateId { get; set; }

    /// <summary>Когда напомнить о самочувствии (только для своих прививок) — «напомнить через 7
    /// дней» из шторки после сохранения; null — не запрошено.</summary>
    public DateTime? WellbeingCheckAt { get; set; }

    /// <summary>Напоминание о самочувствии уже отправлено — одноразовое, как LowStockNotifiedAt у MedicationCourse.</summary>
    public bool WellbeingCheckSent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
