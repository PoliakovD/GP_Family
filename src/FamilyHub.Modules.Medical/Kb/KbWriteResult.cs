namespace FamilyHub.Modules.Medical.Kb;

/// <summary>Итог попытки записи в общий справочник — либо успех (Id строки), либо отказ (payload
/// не прошёл проверку на персональный контекст — см. KbWriter).</summary>
public record KbWriteResult(bool Success, Guid? KbId, string? RejectionReason)
{
    public static KbWriteResult Ok(Guid kbId) => new(true, kbId, null);
    public static KbWriteResult Rejected(string reason) => new(false, null, reason);
}

/// <summary>Проекция raw SQL SELECT "Id" — вспомогательная строка после upsert (см. KbWriter).</summary>
internal sealed class KbIdRow
{
    public Guid Id { get; set; }
}
