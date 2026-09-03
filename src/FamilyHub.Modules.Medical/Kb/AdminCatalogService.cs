using System.Text.Json;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Modules.Medical.Kb;

/// <summary>
/// Ручная правка справочников после ИИ из админки (§3 плана) — единственный писатель, кроме
/// LabAnalyteKbWriter/KbWriter (которые пишет только автоматический конвейер обогащения). Каждое
/// поле, присланное в PUT-запросе, автоматически попадает в LockedFields — следующий проход
/// автообогащения (LabAnalyteKbWriter/KbWriter, см. их class doc) его не тронет.
/// </summary>
public class AdminCatalogService(AppDbContext db)
{
    // --- Показатели ---

    public async Task<AdminLabAnalyteDetail?> GetLabAnalyteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<AdminLabAnalyteRow>($"""
            SELECT a."Id", a."NormalizedName", a."SpecimenKbId", s."DisplayName" AS "SpecimenDisplayName",
                   a."DisplayName", a."PayloadJson", a."Source", a."Aliases", a."LockedFields", a."PayloadVersion",
                   a."CreatedAt", a."UpdatedAt"
            FROM kb.global_lab_analytes_kb a
            LEFT JOIN kb.global_specimens_kb s ON s."Id" = a."SpecimenKbId"
            WHERE a."Id" = {id}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDetail(row);
    }

    public async Task<(AdminKbEditResult Result, AdminLabAnalyteDetail? Detail, string? Reason)> UpdateLabAnalyteAsync(
        Guid id, AdminKbEditRequest request, CancellationToken ct = default)
    {
        if (request.PayloadJson is not null && !IsValidJson(request.PayloadJson))
            return (AdminKbEditResult.InvalidPayloadJson, null, null);

        // Тот же гейт на персональный контекст, что у автоматических writer'ов (см.
        // KbIsolationGuard) — админ доверенный актор, но справочник общий на всех пользователей,
        // случайно вставленный номер телефона/e-mail из буфера обмена не должен туда попасть.
        // Только строковые листья PayloadJson — не сырой JSON-текст целиком, иначе легитимные
        // числа refRanges (например, "9000000" лейкоцитов) ложно триггерили бы LongDigitsPattern.
        // Проверяется ДО похода в БД (как у KbWriter/LabAnalyteKbWriter) — безопасно для SQLite-юнит-тестов.
        var violation = KbIsolationGuard.FindViolation(
            [request.DisplayName, .. ExtractJsonStringLeaves(request.PayloadJson), .. request.Aliases ?? []]);
        if (violation is not null) return (AdminKbEditResult.IsolationViolation, null, violation);

        var existing = await db.Database.SqlQuery<AdminLabAnalyteRow>($"""
            SELECT "Id", "NormalizedName", "SpecimenKbId", NULL AS "SpecimenDisplayName", "DisplayName",
                   "PayloadJson", "Source", "Aliases", "LockedFields", "PayloadVersion", "CreatedAt", "UpdatedAt"
            FROM kb.global_lab_analytes_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);
        if (existing is null) return (AdminKbEditResult.NotFound, null, null);

        var displayName = request.DisplayName ?? existing.DisplayName;
        var payloadJson = request.PayloadJson ?? existing.PayloadJson;
        var aliases = request.Aliases is null
            ? existing.Aliases
            : request.Aliases.Select(LabAnalyteNormalizer.Normalize).Where(a => a.Length > 0).Distinct().ToArray();

        var lockedFields = existing.LockedFields.ToHashSet();
        if (request.DisplayName is not null) lockedFields.Add("displayName");
        if (request.PayloadJson is not null) lockedFields.Add("payload");
        if (request.Aliases is not null) lockedFields.Add("aliases");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_lab_analytes_kb SET
                "DisplayName" = {displayName}, "PayloadJson" = {payloadJson}::jsonb,
                "Aliases" = {aliases}, "LockedFields" = {lockedFields.ToArray()}, "UpdatedAt" = {DateTime.UtcNow}
            WHERE "Id" = {id}
            """, ct);

        return (AdminKbEditResult.Ok, await GetLabAnalyteAsync(id, ct), null);
    }

    public async Task<bool> UnlockLabAnalyteFieldAsync(Guid id, string field, CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_lab_analytes_kb SET "LockedFields" = array_remove("LockedFields", {field}) WHERE "Id" = {id}
            """, ct);
        return affected > 0;
    }

    public async Task<bool> DeleteLabAnalyteAsync(Guid id, CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM kb.global_lab_analytes_kb WHERE "Id" = {id}
            """, ct);
        return affected > 0;
    }

    // --- Медикаменты ---

    public async Task<AdminMedicationDetail?> GetMedicationAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<AdminMedicationRow>($"""
            SELECT "Id", "NormalizedName", "DisplayName", "PayloadJson", "Source", "Aliases", "LockedFields",
                   "PayloadVersion", "CreatedAt", "UpdatedAt"
            FROM kb.global_medications_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDetail(row);
    }

    public async Task<(AdminKbEditResult Result, AdminMedicationDetail? Detail, string? Reason)> UpdateMedicationAsync(
        Guid id, AdminKbEditRequest request, CancellationToken ct = default)
    {
        if (request.PayloadJson is not null && !IsValidJson(request.PayloadJson))
            return (AdminKbEditResult.InvalidPayloadJson, null, null);

        // Проверяется ДО похода в БД (как у KbWriter/LabAnalyteKbWriter) — безопасно для
        // SQLite-юнит-тестов, см. AdminLabAnalyteAsync выше.
        var violation = KbIsolationGuard.FindViolation(
            [request.DisplayName, .. ExtractJsonStringLeaves(request.PayloadJson), .. request.Aliases ?? []]);
        if (violation is not null) return (AdminKbEditResult.IsolationViolation, null, violation);

        var existing = await db.Database.SqlQuery<AdminMedicationRow>($"""
            SELECT "Id", "NormalizedName", "DisplayName", "PayloadJson", "Source", "Aliases", "LockedFields",
                   "PayloadVersion", "CreatedAt", "UpdatedAt"
            FROM kb.global_medications_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);
        if (existing is null) return (AdminKbEditResult.NotFound, null, null);

        var displayName = request.DisplayName ?? existing.DisplayName;
        var payloadJson = request.PayloadJson ?? existing.PayloadJson;
        var aliases = request.Aliases is null
            ? existing.Aliases
            : request.Aliases.Select(MedicationNameNormalizer.Normalize).Where(a => a.Length > 0).Distinct().ToArray();

        var lockedFields = existing.LockedFields.ToHashSet();
        if (request.DisplayName is not null) lockedFields.Add("displayName");
        if (request.PayloadJson is not null) lockedFields.Add("payload");
        if (request.Aliases is not null) lockedFields.Add("aliases");

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_medications_kb SET
                "DisplayName" = {displayName}, "PayloadJson" = {payloadJson}::jsonb,
                "Aliases" = {aliases}, "LockedFields" = {lockedFields.ToArray()}, "UpdatedAt" = {DateTime.UtcNow}
            WHERE "Id" = {id}
            """, ct);

        return (AdminKbEditResult.Ok, await GetMedicationAsync(id, ct), null);
    }

    public async Task<bool> UnlockMedicationFieldAsync(Guid id, string field, CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_medications_kb SET "LockedFields" = array_remove("LockedFields", {field}) WHERE "Id" = {id}
            """, ct);
        return affected > 0;
    }

    public async Task<bool> DeleteMedicationAsync(Guid id, CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM kb.global_medications_kb WHERE "Id" = {id}
            """, ct);
        return affected > 0;
    }

    /// <summary>Все строковые листья JSON (рекурсивно, объекты/массивы) — для сверки с
    /// KbIsolationGuard без ложных срабатываний на числовые поля (refRanges и т.п.). Null/невалидный
    /// JSON — пустая последовательность, IsValidJson уже отсеял невалидный до вызова.</summary>
    private static IEnumerable<string> ExtractJsonStringLeaves(string? json)
    {
        if (string.IsNullOrEmpty(json)) yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            foreach (var leaf in WalkStrings(doc.RootElement)) yield return leaf;
        }

        static IEnumerable<string> WalkStrings(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var s = element.GetString();
                    if (s is not null) yield return s;
                    break;
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                        foreach (var leaf in WalkStrings(prop.Value)) yield return leaf;
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        foreach (var leaf in WalkStrings(item)) yield return leaf;
                    break;
            }
        }
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AdminLabAnalyteDetail ToDetail(AdminLabAnalyteRow r) => new(
        r.Id, r.NormalizedName, r.SpecimenKbId, r.SpecimenDisplayName, r.DisplayName, r.PayloadJson, r.Source,
        r.Aliases, r.LockedFields, r.PayloadVersion, r.CreatedAt, r.UpdatedAt);

    private static AdminMedicationDetail ToDetail(AdminMedicationRow r) => new(
        r.Id, r.NormalizedName, r.DisplayName, r.PayloadJson, r.Source, r.Aliases, r.LockedFields,
        r.PayloadVersion, r.CreatedAt, r.UpdatedAt);
}
