using FamilyHub.Contracts.Events;
using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Audit;
using FamilyHub.Infrastructure.Authorization;
using FamilyHub.Infrastructure.Messaging;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.MedicalRecords;

/// <summary>
/// Мед-анализы — персональный ресурс (раздел 4.2 брифа): принадлежат пользователю, НЕ
/// семье, приватны по умолчанию. Шарингом и скрытием управляет ТОЛЬКО владелец — даже
/// админ семьи сюда не лезет (инвариант 2). Видимость — дословно по разделу 6 брифа, плюс два
/// прямых канала без L1-шаринга: подопечный семьи (FamilyDependentId) и назначение конкретному
/// участнику (TargetUserId) — см. VisibleRecordsQuery. OwnerUserId в обоих случаях остаётся за
/// тем, кто физически загрузил запись — только он безусловно удаляет (см. DeleteAsync).
/// </summary>
public class MedicalRecordService(
    AppDbContext db,
    IFamilyAccessService access,
    IDomainEventPublisher publisher,
    IMedicalAuditWriter audit,
    IRussianTextSearcher searcher,
    IFileStorage storage,
    ILogger<MedicalRecordService> logger)
{
    /// <summary>
    /// Видно, если: владелец, ИЛИ запись назначена лично этому пользователю (TargetUserId), ИЛИ
    /// (мои анализы расшарены этой семье И я в ней состою активным членом И запись не скрыта
    /// именно от неё), ИЛИ запись привязана к подопечному семьи, где я активный член (видна всей
    /// семье подопечного автоматически, без L1-шаринга — подопечный не может сам "расшарить").
    /// Опциональный <paramref name="kind"/> — фильтр по виду записи (анализ/посещение врача);
    /// Kind не зашифрован, поэтому фильтруется прямо в SQL, до расшифровки остальных полей.
    /// </summary>
    private IQueryable<MedicalRecord> VisibleRecordsQuery(Guid userId, MedicalRecordKind? kind = null)
    {
        var query = db.MedicalRecords.AsNoTracking().Where(r =>
            r.OwnerUserId == userId
            || r.TargetUserId == userId
            || db.FamilyMedicalShares.Any(share =>
                   share.OwnerUserId == r.OwnerUserId &&
                   db.FamilyMembers.Any(m =>
                       m.FamilyId == share.FamilyId &&
                       m.UserId == userId &&
                       m.Status == MemberStatus.Active) &&
                   !db.MedicalRecordHiddens.Any(h =>
                       h.MedicalRecordId == r.Id &&
                       h.FamilyId == share.FamilyId))
            || (r.FamilyDependentId != null && db.FamilyMembers.Any(m =>
                   m.UserId == userId &&
                   m.Status == MemberStatus.Active &&
                   db.FamilyDependents.Any(d => d.Id == r.FamilyDependentId && d.FamilyId == m.FamilyId))));

        return kind is null ? query : query.Where(r => r.Kind == kind);
    }

    /// <summary>
    /// HiddenFamilyIds (L2) отдаётся только владельцу записи — это его личная настройка доступа,
    /// а не то, что должны видеть другие члены семьи, которым запись расшарена.
    /// </summary>
    public async Task<List<MedicalRecordDto>> GetVisibleRecordsAsync(
        Guid userId, MedicalRecordKind? kind = null, CancellationToken ct = default)
    {
        var records = await VisibleRecordsQuery(userId, kind).ToListAsync(ct);

        // Аудит (задача 2.7): факт просмотра ЧУЖИХ (расшаренных) записей — по владельцу.
        var foreignOwnerIds = records.Select(r => r.OwnerUserId).Where(o => o != userId).Distinct().ToList();
        if (foreignOwnerIds.Count > 0)
        {
            foreach (var ownerId in foreignOwnerIds)
                audit.Enqueue(userId, MedicalAccessAction.ViewList, ownerUserId: ownerId);
            await db.SaveChangesAsync(ct);
        }

        var ownRecordIds = records.Where(r => r.OwnerUserId == userId).Select(r => r.Id).ToList();
        var hiddenRows = await db.MedicalRecordHiddens
            .Where(h => ownRecordIds.Contains(h.MedicalRecordId))
            .ToListAsync(ct);
        var hiddenByRecord = hiddenRows
            .GroupBy(h => h.MedicalRecordId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(h => h.FamilyId).ToList());

        var personNames = await ResolvePersonNamesAsync(records, userId, ct);

        return records
            .Select(r => ToDto(
                r,
                r.OwnerUserId == userId && hiddenByRecord.TryGetValue(r.Id, out var ids) ? ids : [],
                personNames[r.Id]))
            .ToList();
    }

    /// <summary>
    /// Отображаемое имя пациента (v2 — MedicalRecord.PersonName убран, идентичность выражается
    /// целиком через OwnerUserId/FamilyDependentId/TargetUserId) — резолвится на чтение, а не
    /// хранится: подопечный/участник семьи может переименоваться в профиле, и отображение должно
    /// это подхватывать, а не хранить устаревшую копию. Батч на весь список записей — одним
    /// запросом на FamilyDependent и одним на User, а не N+1.
    /// </summary>
    public async Task<Dictionary<Guid, string>> ResolvePersonNamesAsync(
        IReadOnlyList<MedicalRecord> records, Guid viewerUserId, CancellationToken ct = default)
    {
        var dependentIds = records.Where(r => r.FamilyDependentId is not null)
            .Select(r => r.FamilyDependentId!.Value).Distinct().ToList();
        var userIds = records.Where(r => r.FamilyDependentId is null)
            .Select(r => r.TargetUserId ?? r.OwnerUserId).Distinct().ToList();

        var dependentNames = dependentIds.Count == 0
            ? []
            : (await db.FamilyDependents.AsNoTracking().Where(d => dependentIds.Contains(d.Id)).ToListAsync(ct))
                .ToDictionary(d => d.Id, d => FormatName(d.FirstName, d.LastName, null));

        var userNames = userIds.Count == 0
            ? []
            : (await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync(ct))
                .ToDictionary(u => u.Id, u => FormatName(u.FirstName, u.LastName, u.MiddleName));

        var result = new Dictionary<Guid, string>();
        foreach (var r in records)
        {
            if (r.FamilyDependentId is { } depId)
            {
                result[r.Id] = dependentNames.TryGetValue(depId, out var dn) ? dn : "Без имени";
                continue;
            }

            var uid = r.TargetUserId ?? r.OwnerUserId;
            var name = userNames.TryGetValue(uid, out var un) ? un : string.Empty;
            if (name.Length > 0) { result[r.Id] = name; continue; }

            // Профиль не заполнен: для себя — понятная подпись, для чужого участника — нейтральная.
            result[r.Id] = r.TargetUserId is null ? "Я" : "Без имени";
        }
        return result;
    }

    private static string FormatName(string? firstName, string? lastName, string? middleName)
    {
        var parts = new[] { lastName, firstName, middleName }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(' ', parts);
    }

    /// <summary>Автоподсказка «Врач» в форме создания записи — только доктора, которых ЭТОТ
    /// пользователь уже вводил в СВОИХ записях (v2). In-memory Distinct после расшифровки, не SQL
    /// DISTINCT: Doctor — [Encrypted], шифротекст недетерминирован (ADR-0002), SQL DISTINCT по нему
    /// бессмыслен. Объём — собственные записи одного человека, расшифровка всех подряд безопасна
    /// (тот же приём, что SearchAsync). Только СВОИ (не VisibleRecordsQuery) — это подсказка про
    /// «кого я уже вводил», а не про всех врачей, которых пользователь когда-либо видел в чужих
    /// расшаренных записях.</summary>
    public async Task<List<string>> GetDoctorSuggestionsAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var doctors = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerUserId && r.Doctor != null)
            .Select(r => r.Doctor!)
            .ToListAsync(ct);

        return doctors
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<bool> IsVisibleToAsync(Guid recordId, Guid userId, CancellationToken ct = default) =>
        VisibleRecordsQuery(userId).AnyAsync(r => r.Id == recordId, ct);

    /// <summary>Id видимых записей — переиспользуется поиском по показателям (ветка
    /// medicalrecords, SearchService.SearchIndicatorsAsync): LabIndicators.AnalyteKey не
    /// зашифрован и фильтруется прямо raw SQL, но сам scope доступа обязан идти через
    /// тот же единственный предикат видимости, а не собственную копию.</summary>
    public Task<List<Guid>> GetVisibleRecordIdsAsync(Guid userId, MedicalRecordKind? kind = null, CancellationToken ct = default) =>
        VisibleRecordsQuery(userId, kind).Select(r => r.Id).ToListAsync(ct);

    /// <summary>
    /// Поиск по видимым медкартам (этап 3, ADR-0003). PersonName/Doctor/Description зашифрованы
    /// at-rest (ADR-0002) — Postgres-FTS по ним невозможен, поэтому поиск строится in-memory:
    /// грузим ТОЛЬКО записи в scope пользователя (реюз VisibleRecordsQuery — тот же инвариант
    /// доступа, что и у GetVisibleRecordsAsync), EF расшифровывает поля конвертером при
    /// материализации, дальше матчим через IRussianTextSearcher (морфология + опечатки OCR).
    /// Объём мал (записи одной семьи/пользователя) — расшифровка всех подряд безопасна.
    /// Опциональный <paramref name="kind"/> сужает расшифровку до одного вида — важно для
    /// SearchService: types=visit не должен расшифровывать вообще ни одного анализа, и наоборот.
    /// </summary>
    public async Task<List<MedicalRecordSearchHit>> SearchAsync(
        Guid userId, string query, MedicalRecordKind? kind = null, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var records = await VisibleRecordsQuery(userId, kind).ToListAsync(ct);

        // Аудит просмотра чужих (расшаренных) записей — тот же инвариант, что в GetVisibleRecordsAsync:
        // поиск по чужой медкарте — тоже факт доступа к ней.
        var foreignOwnerIds = records.Select(r => r.OwnerUserId).Where(o => o != userId).Distinct().ToList();
        if (foreignOwnerIds.Count > 0)
        {
            foreach (var ownerId in foreignOwnerIds)
                audit.Enqueue(userId, MedicalAccessAction.ViewList, ownerUserId: ownerId);
            await db.SaveChangesAsync(ct);
        }

        var personNames = await ResolvePersonNamesAsync(records, userId, ct);

        var hits = new List<MedicalRecordSearchHit>();
        foreach (var record in records)
        {
            var haystack = string.Join(
                ' ', new[] { personNames[record.Id], record.Doctor, record.Title, record.Description }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            var score = searcher.Score(haystack, query);
            if (score > 0)
                hits.Add(new MedicalRecordSearchHit(ToDto(record, [], personNames[record.Id]), score));
        }

        logger.LogDebug(
            "Поиск по медкартам: {UserId} нашёл {Count} из {Total} видимых записей", userId, hits.Count, records.Count);

        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    /// <summary>УРОВЕНЬ 1 (чтение): семьи, которым владелец глобально расшарил свои записи.</summary>
    public Task<List<Guid>> GetSharedFamilyIdsAsync(Guid ownerUserId, CancellationToken ct = default) =>
        db.FamilyMedicalShares.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId)
            .Select(s => s.FamilyId)
            .ToListAsync(ct);

    /// <summary>
    /// OwnerUserId всегда — вызывающий (кто физически загружает), независимо от
    /// FamilyDependentId/TargetUserId — так выполняется правило "владелец = загрузивший" без
    /// отдельной проверки. FamilyDependentId и TargetUserId взаимоисключимы и оба валидируются:
    /// без этого любой мог бы прикрепить запись к чужому подопечному или назначить её постороннему,
    /// зная только id.
    /// </summary>
    public async Task<(MedicalRecordAccessResult Result, MedicalRecordDto? Item)> CreateAsync(
        Guid ownerUserId, CreateMedicalRecordRequest request, CancellationToken ct = default)
    {
        if (request.FamilyDependentId is not null && request.TargetUserId is not null)
        {
            logger.LogWarning(
                "Создание мед-записи отклонено: одновременно заданы FamilyDependentId и TargetUserId ({UserId})",
                ownerUserId);
            return (MedicalRecordAccessResult.InvalidTarget, null);
        }

        if (request.FamilyDependentId is { } dependentId)
        {
            var dependentFamilyId = await db.FamilyDependents.AsNoTracking()
                .Where(d => d.Id == dependentId)
                .Select(d => (Guid?)d.FamilyId)
                .FirstOrDefaultAsync(ct);
            if (dependentFamilyId is null)
            {
                logger.LogWarning("Создание мед-записи: подопечный {DependentId} не найден", dependentId);
                return (MedicalRecordAccessResult.NotFound, null);
            }
            if (!await access.HasRoleAsync(ownerUserId, dependentFamilyId.Value, FamilyRole.Member, ct))
            {
                logger.LogWarning(
                    "Создание мед-записи для подопечного {DependentId} отклонено: {UserId} не состоит в его семье",
                    dependentId, ownerUserId);
                return (MedicalRecordAccessResult.Forbidden, null);
            }
        }

        if (request.TargetUserId is { } targetUserId)
        {
            // Не различаем "юзера не существует" и "нет общей активной семьи" — одинаковый
            // Forbidden без 404, чтобы не давать enumeration чужих userId.
            var sharesActiveFamily = await db.FamilyMembers.AsNoTracking()
                .Where(m => m.UserId == ownerUserId && m.Status == MemberStatus.Active)
                .Select(m => m.FamilyId)
                .Join(
                    db.FamilyMembers.Where(m => m.UserId == targetUserId && m.Status == MemberStatus.Active),
                    familyId => familyId, m => m.FamilyId, (familyId, m) => familyId)
                .AnyAsync(ct);
            if (!sharesActiveFamily)
            {
                logger.LogWarning(
                    "Создание мед-записи для пользователя {TargetUserId} отклонено: нет общей активной семьи с {UserId}",
                    targetUserId, ownerUserId);
                return (MedicalRecordAccessResult.Forbidden, null);
            }
        }

        var record = new MedicalRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Kind = request.Kind,
            RecordDate = request.RecordDate,
            Doctor = request.Doctor,
            Description = request.Description,
            ExtractionStatus = ExtractionStatus.None,
            FamilyDependentId = request.FamilyDependentId,
            TargetUserId = request.TargetUserId,
            CreatedAt = DateTime.UtcNow,
        };
        db.MedicalRecords.Add(record);

        List<Guid> hiddenFamilyIds = [];
        if (request.HideFromFamilyIds is { Count: > 0 })
        {
            // Инвариант 4: разрешены только семьи из пересечения «мои семьи» ∩ «расшаренные».
            var sharedFamilyIds = await db.FamilyMedicalShares
                .Where(s => s.OwnerUserId == ownerUserId)
                .Select(s => s.FamilyId)
                .ToListAsync(ct);
            var myFamilyIds = await access.GetActiveFamilyIdsAsync(ownerUserId, ct);
            hiddenFamilyIds = request.HideFromFamilyIds.Intersect(sharedFamilyIds).Intersect(myFamilyIds).ToList();

            foreach (var familyId in hiddenFamilyIds)
                db.MedicalRecordHiddens.Add(new MedicalRecordHidden
                {
                    Id = Guid.NewGuid(),
                    MedicalRecordId = record.Id,
                    FamilyId = familyId,
                    HiddenAt = DateTime.UtcNow,
                });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Мед-запись {RecordId} создана владельцем {OwnerUserId}", record.Id, ownerUserId);
        var personName = (await ResolvePersonNamesAsync([record], ownerUserId, ct))[record.Id];
        return (MedicalRecordAccessResult.Success, ToDto(record, hiddenFamilyIds, personName));
    }

    /// <summary>УРОВЕНЬ 1: владелец открывает ВСЕ свои анализы выбранной семье одним действием.</summary>
    public async Task<MedicalRecordAccessResult> ShareWithFamilyAsync(Guid ownerUserId, Guid familyId, CancellationToken ct = default)
    {
        // Расшарить можно только семье, в которой сам состоишь.
        if (!await access.HasRoleAsync(ownerUserId, familyId, FamilyRole.Member, ct))
        {
            logger.LogWarning(
                "Шаринг мед-записей отклонён: {UserId} не состоит в семье {FamilyId}", ownerUserId, familyId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var exists = await db.FamilyMedicalShares.AnyAsync(
            s => s.OwnerUserId == ownerUserId && s.FamilyId == familyId, ct);
        if (!exists)
        {
            db.FamilyMedicalShares.Add(new FamilyMedicalShare
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                FamilyId = familyId,
                SharedAt = DateTime.UtcNow,
            });
            // Только при реально созданной шаре (повторный вызов события не порождает).
            await publisher.PublishAsync(new MedicalRecordSharedEvent(familyId, ownerUserId), ct);
            audit.Enqueue(ownerUserId, MedicalAccessAction.Share, ownerUserId: ownerUserId, familyId: familyId);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Пользователь {OwnerUserId} расшарил мед-записи семье {FamilyId}", ownerUserId, familyId);
        }

        return MedicalRecordAccessResult.Success;
    }

    /// <summary>
    /// Отключает шаринг семье. MedicalRecordHidden НЕ чистим (инвариант 5) — при повторном
    /// включении точечно скрытое останется скрытым.
    /// </summary>
    public async Task<MedicalRecordAccessResult> UnshareFamilyAsync(Guid ownerUserId, Guid familyId, CancellationToken ct = default)
    {
        var share = await db.FamilyMedicalShares.FirstOrDefaultAsync(
            s => s.OwnerUserId == ownerUserId && s.FamilyId == familyId, ct);
        if (share is null)
        {
            logger.LogWarning(
                "Отмена шаринга: шаринг {OwnerUserId} -> {FamilyId} не найден", ownerUserId, familyId);
            return MedicalRecordAccessResult.NotFound;
        }

        db.FamilyMedicalShares.Remove(share);
        audit.Enqueue(ownerUserId, MedicalAccessAction.Unshare, ownerUserId: ownerUserId, familyId: familyId);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Пользователь {OwnerUserId} отменил шаринг мед-записей семье {FamilyId}", ownerUserId, familyId);
        return MedicalRecordAccessResult.Success;
    }

    /// <summary>УРОВЕНЬ 2: точечно скрыть запись от выбранных семей (из числа уже расшаренных).</summary>
    public async Task<MedicalRecordAccessResult> HideFromFamiliesAsync(
        Guid ownerUserId, Guid recordId, List<Guid> familyIds, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            logger.LogWarning("Скрытие мед-записи {RecordId}: не найдена (запросил {UserId})", recordId, ownerUserId);
            return MedicalRecordAccessResult.NotFound;
        }

        // Инвариант 2: шарингом и скрытием управляет ТОЛЬКО владелец, даже админ семьи не может.
        if (record.OwnerUserId != ownerUserId)
        {
            logger.LogWarning(
                "Скрытие мед-записи {RecordId} отклонено: {UserId} не владелец", recordId, ownerUserId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var sharedFamilyIds = await db.FamilyMedicalShares
            .Where(s => s.OwnerUserId == ownerUserId && familyIds.Contains(s.FamilyId))
            .Select(s => s.FamilyId)
            .ToListAsync(ct);

        foreach (var familyId in sharedFamilyIds)
        {
            var alreadyHidden = await db.MedicalRecordHiddens.AnyAsync(
                h => h.MedicalRecordId == recordId && h.FamilyId == familyId, ct);
            if (!alreadyHidden)
                db.MedicalRecordHiddens.Add(new MedicalRecordHidden
                {
                    Id = Guid.NewGuid(),
                    MedicalRecordId = recordId,
                    FamilyId = familyId,
                    HiddenAt = DateTime.UtcNow,
                });
        }

        audit.Enqueue(ownerUserId, MedicalAccessAction.Hide, ownerUserId: ownerUserId, medicalRecordId: recordId);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Мед-запись {RecordId} скрыта от семей [{FamilyIds}] владельцем {UserId}",
            recordId, string.Join(',', sharedFamilyIds), ownerUserId);
        return MedicalRecordAccessResult.Success;
    }

    public async Task<MedicalRecordAccessResult> UnhideFromFamiliesAsync(
        Guid ownerUserId, Guid recordId, List<Guid> familyIds, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            logger.LogWarning("Раскрытие мед-записи {RecordId}: не найдена (запросил {UserId})", recordId, ownerUserId);
            return MedicalRecordAccessResult.NotFound;
        }
        if (record.OwnerUserId != ownerUserId)
        {
            logger.LogWarning(
                "Раскрытие мед-записи {RecordId} отклонено: {UserId} не владелец", recordId, ownerUserId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var hidden = db.MedicalRecordHiddens.Where(h => h.MedicalRecordId == recordId && familyIds.Contains(h.FamilyId));
        db.MedicalRecordHiddens.RemoveRange(hidden);
        audit.Enqueue(ownerUserId, MedicalAccessAction.Unhide, ownerUserId: ownerUserId, medicalRecordId: recordId);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Мед-запись {RecordId} раскрыта семьям [{FamilyIds}] владельцем {UserId}",
            recordId, string.Join(',', familyIds), ownerUserId);
        return MedicalRecordAccessResult.Success;
    }

    /// <summary>
    /// Безусловное удаление — только владелец (кто физически загрузил), НЕЗАВИСИМО от того, кому
    /// запись сейчас видна (TargetUserId-получатель, вся семья подопечного, расшаренная семья).
    /// Ни назначение, ни привязка к подопечному, ни L1-шаринг не дают права на удаление — это и
    /// есть смысл "владелец = загрузивший" из строгого правила. Чистка вложений — тот же паттерн,
    /// что FamilyDependentService.DeleteAsync/AccountService.DeleteAccountAsync: собрать ключи
    /// хранилища ДО удаления строк → транзакция → коммит → best-effort удаление блобов.
    /// </summary>
    public async Task<MedicalRecordAccessResult> DeleteAsync(Guid ownerUserId, Guid recordId, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null)
        {
            logger.LogWarning("Удаление мед-записи {RecordId}: не найдена (запросил {UserId})", recordId, ownerUserId);
            return MedicalRecordAccessResult.NotFound;
        }

        if (record.OwnerUserId != ownerUserId)
        {
            logger.LogWarning(
                "Удаление мед-записи {RecordId} отклонено: {UserId} не владелец", recordId, ownerUserId);
            return MedicalRecordAccessResult.Forbidden;
        }

        var storageKeys = await db.FileAttachments
            .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId)
            .Select(a => a.StorageKey)
            .ToListAsync(ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.FileAttachments
                .Where(a => a.OwnerType == FileOwnerType.MedicalRecord && a.OwnerId == recordId)
                .ExecuteDeleteAsync(ct);
            // MedicalRecordHidden по этой записи — каскадом FK (MedicalRecordHiddenConfiguration).
            await db.MedicalRecords.Where(r => r.Id == recordId).ExecuteDeleteAsync(ct);
            await audit.WriteAsync(ownerUserId, MedicalAccessAction.Delete, ownerUserId: ownerUserId, medicalRecordId: recordId, ct: ct);
            await tx.CommitAsync(ct);
        }

        foreach (var key in storageKeys)
        {
            try
            {
                await storage.DeleteAsync(key, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Удаление мед-записи {RecordId}: не удалось удалить блоб {StorageKey}", recordId, key);
            }
        }

        logger.LogInformation(
            "Мед-запись {RecordId} удалена владельцем {UserId} ({Files} файлов)", recordId, ownerUserId, storageKeys.Count);
        return MedicalRecordAccessResult.Success;
    }

    private static MedicalRecordDto ToDto(MedicalRecord r, IReadOnlyList<Guid> hiddenFamilyIds, string personName) =>
        new(r.Id, r.OwnerUserId, r.Kind, personName, r.RecordDate, r.Doctor, r.Title, r.Description,
            r.ExtractionStatus, r.CreatedAt, hiddenFamilyIds, r.FamilyDependentId, r.TargetUserId);
}
