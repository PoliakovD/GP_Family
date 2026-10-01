using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Чтение полной строки справочника в виде снимка (<see cref="KbRecordSnapshot"/>) — общая точка для
/// писателей kb (журнал «было/стало»), каталога админки и отката (ADR-0018). Raw SQL, как и весь доступ
/// к kb: Aliases/LockedFields/search_vector вне EF-модели.
/// </summary>
internal static class KbRowStore
{
    public static async Task<KbRecordSnapshot?> ReadMedicationAsync(AppDbContext db, string normalizedName, CancellationToken ct) =>
        ToSnapshot(await db.Database.SqlQuery<KbSnapshotRow>($"""
            SELECT "Id", "NormalizedName", NULL::uuid AS "SpecimenKbId", "DisplayName", "PayloadJson", "Source",
                   "Aliases", "LockedFields", "PayloadVersion", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "CreatedAt"
            FROM kb.global_medications_kb WHERE "NormalizedName" = {normalizedName}
            """).FirstOrDefaultAsync(ct));

    public static async Task<KbRecordSnapshot?> ReadMedicationByIdAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        ToSnapshot(await db.Database.SqlQuery<KbSnapshotRow>($"""
            SELECT "Id", "NormalizedName", NULL::uuid AS "SpecimenKbId", "DisplayName", "PayloadJson", "Source",
                   "Aliases", "LockedFields", "PayloadVersion", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "CreatedAt"
            FROM kb.global_medications_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct));

    public static async Task<KbRecordSnapshot?> ReadLabAnalyteAsync(
        AppDbContext db, string normalizedName, Guid specimenKbId, CancellationToken ct) =>
        ToSnapshot(await db.Database.SqlQuery<KbSnapshotRow>($"""
            SELECT "Id", "NormalizedName", "SpecimenKbId", "DisplayName", "PayloadJson", "Source",
                   "Aliases", "LockedFields", "PayloadVersion", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "CreatedAt"
            FROM kb.global_lab_analytes_kb WHERE "NormalizedName" = {normalizedName} AND "SpecimenKbId" = {specimenKbId}
            """).FirstOrDefaultAsync(ct));

    public static async Task<KbRecordSnapshot?> ReadLabAnalyteByIdAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        ToSnapshot(await db.Database.SqlQuery<KbSnapshotRow>($"""
            SELECT "Id", "NormalizedName", "SpecimenKbId", "DisplayName", "PayloadJson", "Source",
                   "Aliases", "LockedFields", "PayloadVersion", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash", "CreatedAt"
            FROM kb.global_lab_analytes_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct));

    /// <summary>Проверка человеком остаётся в силе, только если итоговый payload после автообогащения совпадает
    /// с тем, что проверяли (ADR-0018: «если автообогащение меняет payload — сброс в AiUnverified»).</summary>
    public static bool KeepVerification(KbRecordSnapshot? before, string finalPayloadJson) =>
        before is { VerificationStatus: not KbVerificationStatus.AiUnverified, VerifiedPayloadHash: not null }
        && before.VerifiedPayloadHash == KbPayloadHash.Compute(finalPayloadJson);

    /// <summary>Изменилось ли что-то, что стоит записи в журнале (UpdatedAt не в счёт).</summary>
    public static bool Differs(KbRecordSnapshot? before, KbRecordSnapshot? after)
    {
        if (before is null || after is null) return before is null != after is null;
        return before.DisplayName != after.DisplayName
            || before.Source != after.Source
            || before.PayloadVersion != after.PayloadVersion
            || before.VerificationStatus != after.VerificationStatus
            || KbPayloadHash.Compute(before.PayloadJson) != KbPayloadHash.Compute(after.PayloadJson)
            || !before.Aliases.OrderBy(a => a).SequenceEqual(after.Aliases.OrderBy(a => a))
            || !before.LockedFields.OrderBy(a => a).SequenceEqual(after.LockedFields.OrderBy(a => a));
    }

    private static KbRecordSnapshot? ToSnapshot(KbSnapshotRow? r) => r is null ? null : new KbRecordSnapshot(
        r.Id, r.NormalizedName, r.SpecimenKbId, r.DisplayName, r.PayloadJson, r.Source, r.Aliases, r.LockedFields,
        r.PayloadVersion, (KbVerificationStatus)r.VerificationStatus, r.VerifiedAt, r.VerifiedPayloadHash, r.CreatedAt);

    internal sealed class KbSnapshotRow
    {
        public Guid Id { get; set; }
        public string NormalizedName { get; set; } = string.Empty;
        public Guid? SpecimenKbId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = "{}";
        public string Source { get; set; } = string.Empty;
        public string[] Aliases { get; set; } = [];
        public string[] LockedFields { get; set; } = [];
        public int PayloadVersion { get; set; }
        public int VerificationStatus { get; set; }
        public DateTime? VerifiedAt { get; set; }
        public string? VerifiedPayloadHash { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
