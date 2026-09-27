namespace FamilyHub.Domain.Entities;

/// <summary>
/// Скан сертификата прививок (одно или несколько фото/страниц), загруженный для распознавания.
/// Сами файлы — <see cref="FileAttachment"/> с <c>OwnerType = VaccinationCertificate</c>. Несколько
/// <see cref="Vaccination"/> могут ссылаться на один и тот же сертификат
/// (<see cref="Vaccination.CertificateId"/>) — файл показывается у каждой найденной прививки без
/// копирования блоба. Субъект — как у <see cref="Vaccination"/>, ровно один из
/// <see cref="SubjectUserId"/>/<see cref="FamilyDependentId"/>.
/// </summary>
public class VaccinationCertificate
{
    public Guid Id { get; set; }

    public Guid? SubjectUserId { get; set; }

    public Guid? FamilyDependentId { get; set; }

    public Guid? FamilyId { get; set; }

    public Guid UploadedByUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
