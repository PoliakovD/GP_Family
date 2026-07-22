using FamilyHub.Domain.Enums;

namespace FamilyHub.Domain.Entities;

/// <summary>
/// Строка аудита доступа к медицинским данным (задача 2.7): кто (ActorUserId), к чьим
/// (OwnerUserId), когда (OccurredAt), что делал (Action). Только UUID и enum — ни ПДн,
/// ни содержимого медданных (acceptance 2.7). Без FK: строки переживают удаление
/// пользователей/записей (обезличенное доказательство доступа).
/// </summary>
public class MedicalAccessAudit
{
    public long Id { get; set; }

    /// <summary>Кто получил доступ.</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>Владелец данных, к которым был доступ (null — действие не о чужих данных, напр. Export).</summary>
    public Guid? OwnerUserId { get; set; }

    public Guid? MedicalRecordId { get; set; }

    public Guid? AttachmentId { get; set; }

    public Guid? FamilyId { get; set; }

    public MedicalAccessAction Action { get; set; }

    public DateTime OccurredAt { get; set; }
}
