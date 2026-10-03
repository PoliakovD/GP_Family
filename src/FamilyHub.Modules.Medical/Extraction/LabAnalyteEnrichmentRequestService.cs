using FamilyHub.Domain.Entities;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Точка входа конвейера обогащения справочника показателей (ветка medicalrecords) — вызывается
/// из MedicalDocumentExtractionProcessor на этапе Linking при промахе поиска в kb.global_lab_analytes_kb.
/// Зеркало EnrichmentRequestService.EnqueueAsync (этап 4): дедуп на уровне БД (частичный уникальный
/// индекс по (NormalizedName, SpecimenKbId) среди Pending/Running, см. LabAnalyteEnrichmentJobConfiguration) —
/// второй анализ с тем же непризнанным показателем И тем же источником молча становится no-op,
/// а не вторым внешним запросом; другой источник того же показателя — отдельная задача.
///
/// ЕДИНСТВЕННАЯ точка входа в конвейер (пересборка enrich-пайплайна) — здесь стоит жёсткий гейт:
/// источник, не подтверждённый SpecimenResolver выше порога уверенности (SpecimenKbId ==
/// SpecimenContextIds.Unresolved), никогда не ставится в очередь внешнего поиска. Это гарантирует
/// требование "не создавать справочник по неопределённому источнику" на уровне одной проверки, а
/// не на каждом вызывающем месте по отдельности.
///
/// Второй гейт — уже проваленная попытка по той же паре (см. RequestAsync ниже): частичный
/// уникальный индекс дедупит только ПОКА задача жива (Pending/Running/Deferred), а Failed-строка
/// из-под него выпадает — без этой проверки повторное извлечение того же документа (или новый
/// документ с тем же показателем) заводило бы новую Failed-задачу с той же причиной на каждый
/// прогон (найдено на проде — "Требует внимания" заполнялся десятками одинаковых карточек).
/// </summary>
public class LabAnalyteEnrichmentRequestService(
    AppDbContext db, IBackgroundJobClient backgroundJobs, ILogger<LabAnalyteEnrichmentRequestService> logger)
{
    public async Task RequestAsync(
        string normalizedName, Guid specimenKbId, string sourceDisplayName, Guid? labIndicatorId,
        Guid requestedByUserId, EnrichmentRequestOrigin origin = EnrichmentRequestOrigin.Extraction,
        CancellationToken ct = default, string? unit = null) =>
        await RequestAsync(
            normalizedName, specimenKbId, sourceDisplayName, labIndicatorId, requestedByUserId, force: false, origin, ct, unit);

    /// <summary>force=true — переобогащение уже существующей KB-записи (см. LabAnalyteKbReenrichJob),
    /// а не первичное обогащение промаха. Дедуп на уровне БД тот же — если для этой пары уже есть
    /// Pending/Running-задача (в т.ч. форсированная другим прогоном reenrich), вторая молча
    /// становится no-op, как и обычно. origin (см. EnrichmentRequestOrigin) не входит в дедуп-ключ —
    /// ручной и документный запрос по одному и тому же показателю по-прежнему дедупятся друг с
    /// другом, не заводят параллельных задач.</summary>
    public async Task RequestAsync(
        string normalizedName, Guid specimenKbId, string sourceDisplayName, Guid? labIndicatorId,
        Guid requestedByUserId, bool force, EnrichmentRequestOrigin origin = EnrichmentRequestOrigin.Extraction,
        CancellationToken ct = default, string? unit = null, bool unitGap = false)
    {
        // До любых затрат: слово из шапки бланка или обрезанное название не должны доходить ни до
        // LLM-стражей, ни до платного поиска (см. AnalyteNameQuality).
        var badName = AnalyteNameQuality.RejectReason(sourceDisplayName);
        if (badName is not null)
        {
            logger.LogInformation("Обогащение «{Name}» не поставлено в очередь: {Reason}", sourceDisplayName, badName);
            return;
        }

        // Жёсткий гейт (см. class doc) — источник не резолвлен/не уверен, во внешний поиск и в
        // справочник ничего не уходит. Тихий выход, не исключение: вызывающий код (Linking-этап
        // экстракции) не должен ронять основной конвейер из-за этого.
        if (specimenKbId == SpecimenContextIds.Unresolved)
        {
            logger.LogInformation(
                "Обогащение показателя «{Name}» ({NormalizedName}) пропущено — источник не определён.",
                sourceDisplayName, normalizedName);
            return;
        }

        // Уже пытались и не вышло — без изменений извне (новый доверенный домен, правка промпта)
        // повторная автоматическая попытка даст тот же результат. force=true (переобогащение/
        // reseed) намеренно проходит мимо этой проверки — там цель ИМЕННО повторить попытку.
        // Ручной путь всё равно остаётся: админ видит причину в «Требует внимания» и жмёт
        // «Перезапустить» — тот эндпоинт работает с уже существующей строкой, не создаёт новую.
        var units = await CollectUnitsAsync(normalizedName, specimenKbId, unit, ct);

        // Пробел по единице: справочник уже есть, но норм в нужной единице нет — повторяем обогащение
        // (кэш поиска переиспользуется, платный запрос уходит только если кэш устарел). Один раз на
        // единицу: если задача с этой единицей уже была в любом статусе, повтор ничего не изменит.
        if (unitGap && unit is not null && await db.LabAnalyteEnrichmentJobs.AnyAsync(j =>
                j.NormalizedName == normalizedName && j.SpecimenKbId == specimenKbId && j.Units != null &&
                EF.Functions.ILike(j.Units, "%" + unit + "%"), ct))
            return;

        if (!force)
        {
            var alreadyFailed = await db.LabAnalyteEnrichmentJobs.AnyAsync(j =>
                j.NormalizedName == normalizedName && j.SpecimenKbId == specimenKbId &&
                (j.Status == EnrichmentJobStatus.Failed || j.Status == EnrichmentJobStatus.Skipped), ct);
            if (alreadyFailed)
            {
                logger.LogDebug(
                    "Обогащение показателя «{NormalizedName}» уже проваливалось ранее, новая задача не создаётся", normalizedName);
                return;
            }
        }

        var job = new LabAnalyteEnrichmentJob
        {
            Id = Guid.NewGuid(),
            NormalizedName = normalizedName,
            SpecimenKbId = specimenKbId,
            SourceDisplayName = sourceDisplayName,
            Units = units,
            LabIndicatorId = labIndicatorId,
            RequestedByUserId = requestedByUserId,
            Force = force,
            Origin = origin,
            Status = EnrichmentJobStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.LabAnalyteEnrichmentJobs.Add(job);

        // Та же гарантия, что и у EnrichmentRequestService: Pending-строка и Hangfire-энкью —
        // единая единица отката, сбой постановки задачи (включая недоступность Hangfire) не
        // должен ронять основной конвейер извлечения показателей.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            backgroundJobs.Enqueue<LabAnalyteEnrichmentProcessor>(p => p.RunAsync(job.Id, CancellationToken.None));
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogDebug(ex, "Обогащение показателя «{NormalizedName}» уже в очереди, пропускаем", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogWarning(ex, "Не удалось поставить обогащение показателя «{NormalizedName}» в очередь", normalizedName);
            db.Entry(job).State = EntityState.Detached;
            return;
        }

        logger.LogInformation(
            "Обогащение справочника показателей поставлено в очередь: «{Name}» ({NormalizedName})", sourceDisplayName, normalizedName);
    }

    /// <summary>Все единицы, в которых этот показатель уже встречался (бланки всех пользователей), плюс
    /// текущая, через "; " — в пределах длины колонки.</summary>
    private async Task<string?> CollectUnitsAsync(string normalizedName, Guid specimenKbId, string? current, CancellationToken ct)
    {
        var known = await db.LabIndicators.AsNoTracking()
            .Where(i => i.AnalyteKey == normalizedName && i.SpecimenKbId == specimenKbId && i.Unit != null && i.Unit != "")
            .Select(i => i.Unit!).Distinct().Take(10).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(current)) known.Add(current.Trim());

        var distinct = known.Select(u => u.Trim()).Where(u => u.Length is > 0 and <= 20)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var joined = string.Join("; ", distinct);
        return joined.Length == 0 ? null : joined.Length <= 200 ? joined : joined[..200];
    }
}
