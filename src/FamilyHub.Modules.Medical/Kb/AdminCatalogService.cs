using System.Text.Json;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
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
public partial class AdminCatalogService(AppDbContext db, KbChangeLogService changeLog)
{
    // --- Показатели ---

    public async Task<AdminLabAnalyteDetail?> GetLabAnalyteAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<AdminLabAnalyteRow>($"""
            SELECT a."Id", a."NormalizedName", a."SpecimenKbId", s."DisplayName" AS "SpecimenDisplayName",
                   a."DisplayName", a."PayloadJson", a."Source", a."Aliases", a."LockedFields", a."PayloadVersion",
                   a."CreatedAt", a."UpdatedAt", a."VerificationStatus", a."VerifiedAt", a."VerifiedPayloadHash"
            FROM kb.global_lab_analytes_kb a
            LEFT JOIN kb.global_specimens_kb s ON s."Id" = a."SpecimenKbId"
            WHERE a."Id" = {id}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDetail(row);
    }

    /// <summary>Строка справочника по ключу дедупликации (показатель + биоматериал) — для очереди
    /// «Одобрение» (ADR-0018): показать текущую запись рядом с черновиком для сравнения.</summary>
    public async Task<AdminLabAnalyteDetail?> GetLabAnalyteByKeyAsync(
        string normalizedName, Guid specimenKbId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<AdminLabAnalyteRow>($"""
            SELECT a."Id", a."NormalizedName", a."SpecimenKbId", s."DisplayName" AS "SpecimenDisplayName",
                   a."DisplayName", a."PayloadJson", a."Source", a."Aliases", a."LockedFields", a."PayloadVersion",
                   a."CreatedAt", a."UpdatedAt", a."VerificationStatus", a."VerifiedAt", a."VerifiedPayloadHash"
            FROM kb.global_lab_analytes_kb a
            LEFT JOIN kb.global_specimens_kb s ON s."Id" = a."SpecimenKbId"
            WHERE a."NormalizedName" = {normalizedName} AND a."SpecimenKbId" = {specimenKbId}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDetail(row);
    }

    /// <summary>Строка справочника препаратов по NormalizedName (см. GetLabAnalyteByKeyAsync).</summary>
    public async Task<AdminMedicationDetail?> GetMedicationByNormalizedNameAsync(
        string normalizedName, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<AdminMedicationRow>($"""
            SELECT "Id", "NormalizedName", "DisplayName", "PayloadJson", "Source", "Aliases", "LockedFields",
                   "PayloadVersion", "CreatedAt", "UpdatedAt", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash"
            FROM kb.global_medications_kb WHERE "NormalizedName" = {normalizedName}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDetail(row);
    }

    public async Task<(AdminKbEditResult Result, AdminLabAnalyteDetail? Detail, string? Reason)> UpdateLabAnalyteAsync(
        Guid id, AdminKbEditRequest request, CancellationToken ct = default, string? logNote = null)
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
                   "PayloadJson", "Source", "Aliases", "LockedFields", "PayloadVersion", "CreatedAt", "UpdatedAt",
                   "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash"
            FROM kb.global_lab_analytes_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);
        if (existing is null) return (AdminKbEditResult.NotFound, null, null);

        var displayName = request.DisplayName ?? existing.DisplayName;
        var payloadJson = request.PayloadJson ?? existing.PayloadJson;
        var aliases = request.Aliases is null
            ? existing.Aliases
            : request.Aliases.Select(LabAnalyteNormalizer.NormalizeAnalyteKey).Where(a => a.Length > 0).Distinct().ToArray();

        var lockedFields = existing.LockedFields.ToHashSet();
        if (request.DisplayName is not null) lockedFields.Add("displayName");
        if (request.PayloadJson is not null) ApplyPayloadLock(lockedFields, request.LockedPayloadKeys);
        if (request.Aliases is not null) lockedFields.Add("aliases");

        // Правка человеком = «проверено с правками» (ADR-0018): хэш — от итогового payload.
        var before = await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct);
        var verificationStatus = (int)KbVerificationStatus.AdminEdited;
        var verifiedAt = DateTime.UtcNow;
        var payloadHash = KbPayloadHash.Compute(payloadJson);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_lab_analytes_kb SET
                "DisplayName" = {displayName}, "PayloadJson" = {payloadJson}::jsonb,
                "Aliases" = {aliases}, "LockedFields" = {lockedFields.ToArray()}, "UpdatedAt" = {verifiedAt},
                "VerificationStatus" = {verificationStatus}, "VerifiedAt" = {verifiedAt}, "VerifiedPayloadHash" = {payloadHash}
            WHERE "Id" = {id}
            """, ct);

        var after = await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct);
        await changeLog.RecordAsync(
            KbChangeTarget.LabAnalyteKb, id, displayName, "admin-edit",
            KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), KbChangeLogService.ActorAdmin, logNote, ct: ct);

        return (AdminKbEditResult.Ok, await GetLabAnalyteAsync(id, ct), null);
    }

    /// <summary>Смена биоматериала у статьи справочника (например, ИИ отнёс «белок» к крови, а это моча).
    /// Биоматериал — часть ключа (NormalizedName, SpecimenKbId): если под новым уже есть статья с тем же
    /// названием, смена не выполняется — возвращается она, чтобы админ объединил статьи, а не плодил дубль.
    /// Показатели пользователей, ссылающиеся на статью (KbAnalyteId), остаются со своим SpecimenKbId — как и
    /// при объединении (см. MergeLabAnalytesAsync).</summary>
    public async Task<(AdminSpecimenChangeResult Result, AdminLabAnalyteDetail? Detail, AdminSpecimenChangeConflict? Conflict)>
        ChangeLabAnalyteSpecimenAsync(Guid id, Guid specimenKbId, CancellationToken ct = default)
    {
        var current = await db.GlobalLabAnalytesKb.AsNoTracking()
            .Where(k => k.Id == id).Select(k => new { k.NormalizedName, k.SpecimenKbId }).FirstOrDefaultAsync(ct);
        if (current is null) return (AdminSpecimenChangeResult.NotFound, null, null);
        if (current.SpecimenKbId == specimenKbId) return (AdminSpecimenChangeResult.Ok, await GetLabAnalyteAsync(id, ct), null);
        if (!await db.GlobalSpecimensKb.AnyAsync(s => s.Id == specimenKbId, ct))
            return (AdminSpecimenChangeResult.SpecimenNotFound, null, null);

        var existing = await db.GlobalLabAnalytesKb.AsNoTracking()
            .Where(k => k.Id != id && k.NormalizedName == current.NormalizedName && k.SpecimenKbId == specimenKbId)
            .Select(k => new { k.Id, k.DisplayName }).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return (AdminSpecimenChangeResult.Conflict, null, new AdminSpecimenChangeConflict(existing.Id, existing.DisplayName));

        var before = await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_lab_analytes_kb SET "SpecimenKbId" = {specimenKbId}, "UpdatedAt" = {DateTime.UtcNow}
            WHERE "Id" = {id}
            """, ct);
        var after = await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct);
        await changeLog.RecordAsync(
            KbChangeTarget.LabAnalyteKb, id, before?.DisplayName ?? current.NormalizedName, "admin-edit",
            KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), KbChangeLogService.ActorAdmin,
            "Смена биоматериала", ct: ct);

        return (AdminSpecimenChangeResult.Ok, await GetLabAnalyteAsync(id, ct), null);
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
        var before = await KbRowStore.ReadLabAnalyteByIdAsync(db, id, ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM kb.global_lab_analytes_kb WHERE "Id" = {id}
            """, ct);
        if (affected > 0 && before is not null)
        {
            await changeLog.RecordAsync(
                KbChangeTarget.LabAnalyteKb, id, before.DisplayName, "admin-delete",
                KbChangeLogService.ToJson(before), null, KbChangeLogService.ActorAdmin, ct: ct);
        }

        return affected > 0;
    }

    /// <summary>
    /// Объединение двух строк справочника показателей (например, две записи для одного и того же
    /// анализа, разошедшиеся из-за "грязного" OCR-имени) — проигравшая строка удаляется, её
    /// показатели (LabIndicators.KbAnalyteId — обычное справочное поле, не часть уникального
    /// ключа, редиректить можно без риска коллизии) переезжают на победителя, а её
    /// NormalizedName попадает победителю в Aliases — то же название после следующего OCR найдёт
    /// победителя вместо повторного дубля (см. LabAnalyteKbLookupService.LookupBySpecimenAsync,
    /// уже проверяет ANY(Aliases)). Разные SpecimenKbId у сливаемых строк допускаются намеренно —
    /// это инструмент ручной чистки, админ сам решает, что считать дублем; LabIndicator.SpecimenKbId
    /// самих показателей при этом не трогается, меняется только то, на какую статью справочника
    /// они ссылаются.
    /// </summary>
    public async Task<AdminKbMergeResult> MergeLabAnalytesAsync(Guid loserId, Guid winnerId, CancellationToken ct = default)
    {
        if (loserId == winnerId) return AdminKbMergeResult.SameId;

        var loser = await db.Database.SqlQuery<AdminLabAnalyteRow>($"""
            SELECT "Id", "NormalizedName", "SpecimenKbId", NULL AS "SpecimenDisplayName", "DisplayName",
                   "PayloadJson", "Source", "Aliases", "LockedFields", "PayloadVersion", "CreatedAt", "UpdatedAt",
                   "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash"
            FROM kb.global_lab_analytes_kb WHERE "Id" = {loserId}
            """).FirstOrDefaultAsync(ct);
        var winnerExists = await db.GlobalLabAnalytesKb.AnyAsync(k => k.Id == winnerId, ct);
        if (loser is null || !winnerExists) return AdminKbMergeResult.NotFound;

        // Вызывается и напрямую из админ-эндпоинта, и изнутри GlobalSpecimenKbService.MergeAsync
        // (там — уже в открытой транзакции, вложенный BeginTransactionAsync на том же соединении
        // упал бы) — своя транзакция открывается, только если родитель её ещё не начал.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;

        await db.LabIndicators.Where(i => i.KbAnalyteId == loserId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.KbAnalyteId, winnerId), ct);

        // Ключ/синонимы проигравшего могли остаться от прежнего нормализатора (строка старше его смены) — в
        // победителя они уходят в текущей форме, иначе синоним никогда не совпадёт с ключом поиска.
        var loserAliases = loser.Aliases.Append(loser.NormalizedName)
            .Select(LabAnalyteNormalizer.RenormalizeKey).Where(a => a.Length > 0).Distinct().ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_lab_analytes_kb
            SET "Aliases" = ARRAY(SELECT DISTINCT unnest("Aliases" || {loserAliases})), "UpdatedAt" = {DateTime.UtcNow}
            WHERE "Id" = {winnerId}
            """, ct);

        var loserSnapshot = await KbRowStore.ReadLabAnalyteByIdAsync(db, loserId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM kb.global_lab_analytes_kb WHERE "Id" = {loserId}""", ct);
        if (loserSnapshot is not null)
        {
            await changeLog.RecordAsync(
                KbChangeTarget.LabAnalyteKb, loserId, loserSnapshot.DisplayName, "admin-merge",
                KbChangeLogService.ToJson(loserSnapshot), null, KbChangeLogService.ActorAdmin,
                $"Объединена в запись {winnerId}", ct: ct);
        }

        if (tx is not null)
        {
            await tx.CommitAsync(ct);
            await tx.DisposeAsync();
        }
        return AdminKbMergeResult.Ok;
    }

    /// <summary>Резолвит имена "Что смотрят вместе" (LabAnalyteKbPayload.relatedNames — плоские
    /// строки, не хранимые ссылки, см. class doc LabAnalyteSummary.RelatedAnalytes) в реальные
    /// строки справочника по точному совпадению NormalizedName — тот же приём, что публичный
    /// KbAnalyteCatalogService.ResolveRelatedAsync использует для карточки пользователя, отдельная
    /// копия здесь (не переиспользуем ту приватную), чтобы админка не зависела от internals
    /// публичного read-сервиса. Используется редактором формы, чтобы: (1) при открытии показателя
    /// подсветить, какие из уже сохранённых related-имён реально резолвятся в существующую статью
    /// (а какие — оборванная ссылка/опечатка), и (2) сразу дать кликабельный переход на неё.
    /// Ненайденное имя — Id=null, не ошибка (см. тот же комментарий в публичном резолвере).
    ///
    /// NormalizedName уникально только В ПАРЕ с SpecimenKbId (см. GlobalLabAnalyteKbConfiguration) —
    /// один и тот же ключ ("лейкоциты") легитимно существует под несколькими специминами (кровь/
    /// моча — разные статьи). Прод-баг: наивный ToDictionary(NormalizedName) падал с "An item with
    /// the same key has already been added" ровно на этом. У этого метода (в отличие от публичного
    /// ResolveRelatedAsync) нет "своей" карточки/специмина, относительно которого выбирать —
    /// эндпоинт принимает произвольный список имён без контекста статьи, поэтому при коллизии просто
    /// детерминированно берём первую найденную строку, а не падаем.</summary>
    public async Task<List<AdminRelatedAnalyteMatch>> ResolveRelatedNamesAsync(
        IReadOnlyList<string> names, CancellationToken ct = default)
    {
        if (names.Count == 0) return [];

        var normalizedToOriginal = names
            .Select(name => (Name: name, Normalized: LabAnalyteNormalizer.NormalizeAnalyteKey(name)))
            .Where(p => p.Normalized.Length > 0)
            .ToList();
        if (normalizedToOriginal.Count == 0) return [];

        var normalizedKeys = normalizedToOriginal.Select(p => p.Normalized).Distinct().ToArray();
        var matches = await db.Database.SqlQuery<AdminRelatedMatchRow>($"""
            SELECT a."Id", a."DisplayName", a."NormalizedName", s."DisplayName" AS "SpecimenDisplayName"
            FROM kb.global_lab_analytes_kb a
            LEFT JOIN kb.global_specimens_kb s ON s."Id" = a."SpecimenKbId"
            WHERE a."NormalizedName" = ANY({normalizedKeys})
            """).ToListAsync(ct);
        var byNormalized = matches
            .GroupBy(m => m.NormalizedName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return normalizedToOriginal.Select(p => byNormalized.TryGetValue(p.Normalized, out var m)
            ? new AdminRelatedAnalyteMatch(p.Name, m.Id, m.DisplayName, m.SpecimenDisplayName)
            : new AdminRelatedAnalyteMatch(p.Name, null, null, null)).ToList();
    }

    // --- Медикаменты ---

    public async Task<AdminMedicationDetail?> GetMedicationAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<AdminMedicationRow>($"""
            SELECT "Id", "NormalizedName", "DisplayName", "PayloadJson", "Source", "Aliases", "LockedFields",
                   "PayloadVersion", "CreatedAt", "UpdatedAt", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash"
            FROM kb.global_medications_kb WHERE "Id" = {id}
            """).FirstOrDefaultAsync(ct);

        return row is null ? null : ToDetail(row);
    }

    public async Task<(AdminKbEditResult Result, AdminMedicationDetail? Detail, string? Reason)> UpdateMedicationAsync(
        Guid id, AdminKbEditRequest request, CancellationToken ct = default, string? logNote = null)
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
                   "PayloadVersion", "CreatedAt", "UpdatedAt", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash"
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
        if (request.PayloadJson is not null) ApplyPayloadLock(lockedFields, request.LockedPayloadKeys);
        if (request.Aliases is not null) lockedFields.Add("aliases");

        var before = await KbRowStore.ReadMedicationByIdAsync(db, id, ct);
        var verificationStatus = (int)KbVerificationStatus.AdminEdited;
        var verifiedAt = DateTime.UtcNow;
        var payloadHash = KbPayloadHash.Compute(payloadJson);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_medications_kb SET
                "DisplayName" = {displayName}, "PayloadJson" = {payloadJson}::jsonb,
                "Aliases" = {aliases}, "LockedFields" = {lockedFields.ToArray()}, "UpdatedAt" = {verifiedAt},
                "VerificationStatus" = {verificationStatus}, "VerifiedAt" = {verifiedAt}, "VerifiedPayloadHash" = {payloadHash}
            WHERE "Id" = {id}
            """, ct);

        var after = await KbRowStore.ReadMedicationByIdAsync(db, id, ct);
        await changeLog.RecordAsync(
            KbChangeTarget.MedicationKb, id, displayName, "admin-edit",
            KbChangeLogService.ToJson(before), KbChangeLogService.ToJson(after), KbChangeLogService.ActorAdmin, logNote, ct: ct);

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
        var before = await KbRowStore.ReadMedicationByIdAsync(db, id, ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM kb.global_medications_kb WHERE "Id" = {id}
            """, ct);
        if (affected > 0 && before is not null)
        {
            await changeLog.RecordAsync(
                KbChangeTarget.MedicationKb, id, before.DisplayName, "admin-delete",
                KbChangeLogService.ToJson(before), null, KbChangeLogService.ActorAdmin, ct: ct);
        }

        return affected > 0;
    }

    /// <summary>Объединение дублей справочника медикаментов («Парацетамол» / «Парацетамол-Акрихин»). Препараты
    /// пользователей ссылаются на справочник не по Id, а живым поиском по названию и синонимам (KbLookupService),
    /// поэтому достаточно перенести название и синонимы проигравшего победителю и удалить проигравшего; KbId у
    /// задач обогащения (справочное поле) перенаправляется на победителя.</summary>
    public async Task<AdminKbMergeResult> MergeMedicationsAsync(Guid loserId, Guid winnerId, CancellationToken ct = default)
    {
        if (loserId == winnerId) return AdminKbMergeResult.SameId;

        var loser = await db.Database.SqlQuery<AdminMedicationRow>($"""
            SELECT "Id", "NormalizedName", "DisplayName", "PayloadJson", "Source", "Aliases", "LockedFields",
                   "PayloadVersion", "CreatedAt", "UpdatedAt", "VerificationStatus", "VerifiedAt", "VerifiedPayloadHash"
            FROM kb.global_medications_kb WHERE "Id" = {loserId}
            """).FirstOrDefaultAsync(ct);
        var winnerExists = await db.GlobalMedicationsKb.AnyAsync(k => k.Id == winnerId, ct);
        if (loser is null || !winnerExists) return AdminKbMergeResult.NotFound;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.MedicationEnrichmentJobs.Where(j => j.KbId == loserId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.KbId, winnerId), ct);
        await db.VisitMedicationEnrichmentJobs.Where(j => j.KbId == loserId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.KbId, winnerId), ct);

        var loserAliases = loser.Aliases.Append(loser.NormalizedName).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE kb.global_medications_kb
            SET "Aliases" = ARRAY(SELECT DISTINCT unnest("Aliases" || {loserAliases})), "UpdatedAt" = {DateTime.UtcNow}
            WHERE "Id" = {winnerId}
            """, ct);

        var loserSnapshot = await KbRowStore.ReadMedicationByIdAsync(db, loserId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM kb.global_medications_kb WHERE "Id" = {loserId}""", ct);
        if (loserSnapshot is not null)
        {
            await changeLog.RecordAsync(
                KbChangeTarget.MedicationKb, loserId, loserSnapshot.DisplayName, "admin-merge",
                KbChangeLogService.ToJson(loserSnapshot), null, KbChangeLogService.ActorAdmin,
                $"Объединена в запись {winnerId}", ct: ct);
        }

        await tx.CommitAsync(ct);
        return AdminKbMergeResult.Ok;
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

    /// <summary>Режим формы (LockedPayloadKeys задан) лочит "payload.&lt;key&gt;" по каждому
    /// реально изменённому полю формы; режим JSON (null) лочит "payload" целиком — прежнее
    /// поведение сырого редактора. Если строка уже несёт лок на весь "payload", точечные локи
    /// избыточны поверх уже более широкого — не добавляем их.</summary>
    private static void ApplyPayloadLock(HashSet<string> lockedFields, IReadOnlyList<string>? lockedPayloadKeys)
    {
        if (lockedPayloadKeys is null)
        {
            lockedFields.Add("payload");
            return;
        }
        if (lockedFields.Contains("payload")) return;
        foreach (var key in lockedPayloadKeys)
            if (key.Length > 0) lockedFields.Add($"payload.{key}");
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
        r.Aliases, r.LockedFields, r.PayloadVersion, r.CreatedAt, r.UpdatedAt,
        (KbVerificationStatus)r.VerificationStatus, r.VerifiedAt, IsStale(r.VerificationStatus, r.VerifiedPayloadHash, r.PayloadJson));

    private static AdminMedicationDetail ToDetail(AdminMedicationRow r) => new(
        r.Id, r.NormalizedName, r.DisplayName, r.PayloadJson, r.Source, r.Aliases, r.LockedFields,
        r.PayloadVersion, r.CreatedAt, r.UpdatedAt,
        (KbVerificationStatus)r.VerificationStatus, r.VerifiedAt, IsStale(r.VerificationStatus, r.VerifiedPayloadHash, r.PayloadJson));

    /// <summary>Проверена, но payload с тех пор изменился (например, правка в обход журнала) — проверка устарела.</summary>
    private static bool IsStale(int status, string? verifiedHash, string payloadJson) =>
        status != (int)KbVerificationStatus.AiUnverified && verifiedHash is not null
        && verifiedHash != KbPayloadHash.Compute(payloadJson);
}
