using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Infrastructure.Search;
using FamilyHub.Modules.Medical.Kb;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Enrichment;

/// <summary>
/// Точка входа конвейера обогащения справочника (этап 4) — вызывается сразу после сохранения
/// медикамента (см. MedicationService.CreateAsync/UpdateAsync). Нормализует имя, проверяет
/// справочник, и ТОЛЬКО при промахе/неуверенном совпадении ставит задачу в очередь Hangfire.
/// Дедуп на уровне БД (частичный уникальный индекс по NormalizedName среди Pending/Running/Deferred
/// задач, см. MedicationEnrichmentJobConfiguration) — конкурентное сохранение того же препарата в
/// другой семье молча становится no-op, а не ошибкой и не вторым внешним запросом.
/// Вентиль платного поиска/кулдаун здесь намеренно НЕ проверяются — этим занимается сам
/// MedicationEnrichmentProcessor (у него есть настоящий кэш сниппетов: если по названию уже
/// есть закэшированный поиск, задача выполнится мгновенно и бесплатно, повторно ходить к
/// платному API незачем). Проверка здесь заранее только дублировала бы эту логику.
///
/// Уже проваленная попытка (Failed/Skipped) по тому же NormalizedName блокирует новую задачу —
/// частичный уникальный индекс дедупит только ПОКА задача жива, Failed из-под него выпадает
/// (см. RequestAsync).
/// </summary>
public class EnrichmentRequestService(
    AppDbContext db,
    KbLookupService kbLookup,
    IBackgroundJobClient backgroundJobs,
    ILogger<EnrichmentRequestService> logger) : IEnrichmentRequestService
{
    public async Task RequestAsync(Medication medication, Guid userId, CancellationToken ct = default)
    {
        var normalizedName = MedicationNameNormalizer.Normalize(medication.Name);
        if (normalizedName.Length == 0) return;

        // Candidate (неуверенное нечёткое совпадение) НЕ считается «уже есть знание» — точный
        // ключ NormalizedName у ЭТОГО названия по-прежнему не разрешается напрямую (см. KbLookupService),
        // поэтому конвейер всё равно запускается. Только Hit останавливает конвейер.
        var lookup = await kbLookup.LookupAsync(normalizedName, ct);
        if (lookup.Kind == KbLookupKind.Hit) return;

        // Уже пытались и не вышло — Failed никогда не пишет в KB (Hit выше так и не появится),
        // поэтому без этой проверки КАЖДОЕ сохранение медикамента с тем же названием (в т.ч.
        // правка ExpiryDate без изменения имени — UpdateAsync зовёт RequestAsync безусловно)
        // заводило бы новую Failed-задачу с той же причиной. Ручной путь не страдает —
        // RequestRefreshAsync («Уточнить в справочнике») эту проверку намеренно не делает.
        var alreadyFailed = await db.MedicationEnrichmentJobs.AnyAsync(j =>
            j.NormalizedName == normalizedName &&
            (j.Status == EnrichmentJobStatus.Failed || j.Status == EnrichmentJobStatus.Skipped), ct);
        if (alreadyFailed)
        {
            logger.LogDebug("Обогащение «{NormalizedName}» уже проваливалось ранее, новая задача не создаётся", normalizedName);
            return;
        }

        await EnqueueAsync(NewJob(normalizedName, medication.Name, medication.Id, userId, medication.FamilyId, force: false), ct);
    }

    public async Task<EnrichmentRefreshOutcome> RequestRefreshAsync(Medication medication, Guid userId, CancellationToken ct = default)
    {
        var normalizedName = MedicationNameNormalizer.Normalize(medication.Name);
        if (normalizedName.Length == 0) return EnrichmentRefreshOutcome.NothingToRefresh();

        // Ручной запрос («Уточнить в справочнике», GET/POST /api/medications/{id}/kb/refresh) —
        // в отличие от RequestAsync намеренно НЕ прерывается на Hit: пользователь мог заметить
        // устаревшую/неполную карточку и хочет принудительного повторного обогащения (Force — иначе
        // процессор завершил бы задачу на том же Hit, ничего не сделав). Дедуп на Pending/Running
        // всё равно защищает от повторной постановки, пока предыдущая не завершилась.
        // Если название найдено по синониму — обогащаем саму найденную статью (её ключ), а не заводим
        // рядом вторую под ключом этого написания.
        var lookup = await kbLookup.LookupAsync(normalizedName, ct);
        if (lookup.Kind == KbLookupKind.Hit && lookup.KbId is { } kbId)
        {
            normalizedName = await db.GlobalMedicationsKb.AsNoTracking()
                .Where(k => k.Id == kbId).Select(k => k.NormalizedName).FirstOrDefaultAsync(ct) ?? normalizedName;
        }

        await EnqueueAsync(NewJob(normalizedName, medication.Name, medication.Id, userId, medication.FamilyId, force: true), ct);
        return EnrichmentRefreshOutcome.Requested();
    }

    /// <summary>«Переобогатить» статью из админки: та же задача конвейера, но без пользователя и семьи
    /// (Guid.Empty — уведомлять некого, см. MedicationEnrichmentProcessor) и с Force.</summary>
    public async Task<AdminReenrichResult> RequestKbReenrichAsync(Guid kbId, CancellationToken ct = default)
    {
        var kb = await db.GlobalMedicationsKb.AsNoTracking()
            .Where(k => k.Id == kbId).Select(k => new { k.NormalizedName, k.DisplayName }).FirstOrDefaultAsync(ct);
        if (kb is null) return AdminReenrichResult.NotFound;

        return await EnqueueAsync(NewJob(kb.NormalizedName, kb.DisplayName, null, Guid.Empty, Guid.Empty, force: true), ct)
            ? AdminReenrichResult.Queued
            : AdminReenrichResult.AlreadyQueued;
    }

    private static MedicationEnrichmentJob NewJob(
        string normalizedName, string displayName, Guid? medicationId, Guid userId, Guid familyId, bool force) => new()
    {
        Id = Guid.NewGuid(),
        NormalizedName = normalizedName,
        SourceDisplayName = displayName,
        MedicationId = medicationId,
        RequestedByUserId = userId,
        FamilyId = familyId,
        Force = force,
        Status = EnrichmentJobStatus.Pending,
        CreatedAt = DateTime.UtcNow,
    };

    /// <summary>false — задача не поставлена (уже есть живая на это название либо Hangfire недоступен).</summary>
    private async Task<bool> EnqueueAsync(MedicationEnrichmentJob job, CancellationToken ct)
    {
        var normalizedName = job.NormalizedName;
        db.MedicationEnrichmentJobs.Add(job);

        // Pending-строка и Hangfire-энкью — единая единица отката в явной транзакции (Hangfire
        // использует ОТДЕЛЬНОЕ соединение, не наш EF DbContext — см. докстринг класса, поэтому
        // здесь нет распределённой транзакции с Hangfire, только гарантия для НАШЕЙ вставки).
        // Раньше SaveChangesAsync коммитился сразу, и если backgroundJobs.Enqueue ниже кидал
        // исключение (Hangfire-сторож недоступен), Pending-строка оставалась в БД БЕЗ реальной
        // задачи в очереди — это навсегда блокировало бы дедупом повторные попытки обогащения
        // для того же препарата. Любой сбой здесь (включая недоступность Hangfire) не должен
        // ронять основной сценарий (создание/правку медикамента) — см. аудит
        // module-review-2026-08-02/04-medications-medkits-kb-enrichment-ocr.md, находка 1.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<MedicationEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Уже есть Pending/Running задача на этот NormalizedName (частичный уникальный индекс,
            // тот же приём, что NotificationSendingService.AddIfNewAsync с DedupKey) — no-op.
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex, "Обогащение «{NormalizedName}» уже в очереди, пропускаем", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить обогащение «{NormalizedName}» в очередь", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return false;
        }

        logger.LogInformation(
            "Обогащение справочника поставлено в очередь: «{Name}» ({NormalizedName})", job.SourceDisplayName, normalizedName);
        return true;
    }
}

public enum AdminReenrichResult { Queued, NotFound, AlreadyQueued }
