using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;

namespace FamilyHub.Infrastructure.Audit;

public class MedicalAuditWriter(AppDbContext db) : IMedicalAuditWriter
{
    public void Enqueue(
        Guid actorUserId, MedicalAccessAction action,
        Guid? ownerUserId = null, Guid? medicalRecordId = null, Guid? attachmentId = null, Guid? familyId = null)
    {
        db.Set<MedicalAccessAudit>().Add(new MedicalAccessAudit
        {
            ActorUserId = actorUserId,
            OwnerUserId = ownerUserId,
            MedicalRecordId = medicalRecordId,
            AttachmentId = attachmentId,
            FamilyId = familyId,
            Action = action,
            OccurredAt = DateTime.UtcNow,
        });
    }

    public async Task WriteAsync(
        Guid actorUserId, MedicalAccessAction action,
        Guid? ownerUserId = null, Guid? medicalRecordId = null, Guid? attachmentId = null, Guid? familyId = null,
        CancellationToken ct = default)
    {
        Enqueue(actorUserId, action, ownerUserId, medicalRecordId, attachmentId, familyId);
        await db.SaveChangesAsync(ct);
    }
}
