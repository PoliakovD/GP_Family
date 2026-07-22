using FamilyHub.Domain.Enums;

namespace FamilyHub.Infrastructure.Audit;

/// <summary>
/// Запись аудита доступа к медданным (задача 2.7). Синхронно через общий AppDbContext,
/// НЕ через outbox: аудит не должен теряться; в мутациях строка коммитится одной
/// транзакцией с бизнес-изменением.
/// </summary>
public interface IMedicalAuditWriter
{
    /// <summary>Добавляет строку в контекст без SaveChanges — для мутаций (общий коммит).</summary>
    void Enqueue(
        Guid actorUserId, MedicalAccessAction action,
        Guid? ownerUserId = null, Guid? medicalRecordId = null, Guid? attachmentId = null, Guid? familyId = null);

    /// <summary>Добавляет и сразу сохраняет — для read-путей, где своего SaveChanges нет.</summary>
    Task WriteAsync(
        Guid actorUserId, MedicalAccessAction action,
        Guid? ownerUserId = null, Guid? medicalRecordId = null, Guid? attachmentId = null, Guid? familyId = null,
        CancellationToken ct = default);
}
