using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Каскад п.1a, шаг "дозаполнение задним числом" — запускается после того, как
/// LabAnalyteEnrichmentProcessor пополнил (или подтвердил, что уже содержит) запись
/// kb.global_lab_analytes_kb: показатели, распознанные РАНЬШЕ (пока справочника ещё не было),
/// привязываются к появившейся статье (LabIndicator.KbAnalyteId), а те из них, что застряли на
/// RefSource.None, ещё и пересчитываются тем же каскадом (IndicatorFlagCalculator.PickBestRange →
/// PatientReferenceCalculator), без повторного клика «Распознать» пользователем.
///
/// KbAnalyteId проставляется ВСЕГДА, независимо от RefSource — привязка к статье справочника
/// (панель "Что это"/"Что смотрят вместе") НЕ зависит от того, откуда взят референсный диапазон
/// значения. Показатель с RefSource.Blank (бланк лаборатории напечатал собственный диапазон —
/// самый частый случай на практике) имеет наивысший приоритет в каскаде и никогда не
/// переопределяется справочником, но статья справочника у него всё равно должна открываться.
/// РАНЬШЕ это поле обновлялось только для RefSource.None — показатель с диапазоном из самого
/// бланка так и оставался НЕ привязан к статье навсегда, даже после того, как обогащение
/// благополучно завершалось: панель справки показывала "нет данных", будто обогащение не
/// сработало, хотя оно сработало — просто ссылка не проставлялась.
///
/// Та же очередь "enrichment", что и сам LabAnalyteEnrichmentProcessor — лёгкая задача (KB уже в
/// памяти, только опционально один расчётный LLM-вызов на показатель).
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class RecalculateIndicatorFlagsJob(
    AppDbContext db,
    PatientReferenceCalculator referenceCalculator,
    IPipelineConfigService pipelineConfig,
    ILogger<RecalculateIndicatorFlagsJob> logger)
{
    public async Task RunAsync(Guid kbAnalyteId, CancellationToken ct = default)
    {
        var kb = await db.GlobalLabAnalytesKb.AsNoTracking().FirstOrDefaultAsync(k => k.Id == kbAnalyteId, ct);
        if (kb is null)
        {
            logger.LogWarning("RecalculateIndicatorFlagsJob: kb-запись {KbAnalyteId} не найдена — пропускаем.", kbAnalyteId);
            return;
        }

        // KbAnalyteId уже проставлен — прошлый прогон нашёл эту же запись, но не смог вывести
        // диапазон под пациента (RefSource.None), пробуем досчитать ещё раз (методика/фикс.
        // диапазон в KB могли обновиться следующим обогащением). AnalyteKey+SpecimenKbId без
        // KbAnalyteId — показатель распознан ДО того, как эта KB-запись вообще появилась, ссылку
        // нужно проставить независимо от RefSource (см. class doc — самый частый случай, RefSource.
        // Blank, раньше сюда не попадал вовсе). SpecimenKbId — обязательное условие фолбэка
        // (пересборка enrich-пайплайна): без него "белок" в моче мог бы ошибочно подхватить норму
        // записи "белок" в крови, которая появилась первой.
        var candidates = await db.LabIndicators
            .Where(i => i.KbAnalyteId == kbAnalyteId ||
                        (i.KbAnalyteId == null && i.AnalyteKey == kb.NormalizedName && i.SpecimenKbId == kb.SpecimenKbId))
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        var refRanges = LabAnalyteKbPayload.ParseRefRanges(kb.PayloadJson);
        var instructions = LabAnalyteKbPayload.ParseCalculationInstructions(kb.PayloadJson);

        var updated = 0;
        foreach (var group in candidates.GroupBy(i => i.MedicalRecordId))
        {
            var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == group.Key, ct);
            if (record is null) continue;

            var (ageYears, sex) = await PatientIdentityResolver.ResolveAsync(db, record, ct);

            foreach (var indicator in group)
            {
                // Привязка к статье — БЕЗУСЛОВНО, до проверки RefSource (см. class doc): нужна
                // панели справки независимо от того, откуда взят референсный диапазон значения.
                var changed = indicator.KbAnalyteId is null;
                indicator.KbAnalyteId ??= kbAnalyteId;

                if (indicator.RefSource == RefSource.None)
                {
                    var kbFallback = IndicatorFlagCalculator.PickBestRange(refRanges, ageYears, sex);
                    if (kbFallback is not null)
                    {
                        indicator.Flag = IndicatorFlagCalculator.ApplyCalculatedRange(indicator.ValueRaw, kbFallback.Low, kbFallback.High);
                        indicator.RefSource = RefSource.KbFixed;
                        indicator.RefLowText = kbFallback.Low?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        indicator.RefHighText = kbFallback.High?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        changed = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(instructions) &&
                             await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "patient-reference", ct))
                    {
                        var calculated = await referenceCalculator.CalculateAsync(
                            indicator.DisplayName, instructions, ageYears, sex, indicator.Unit, ct);
                        if (calculated is not null)
                        {
                            indicator.Flag = IndicatorFlagCalculator.ApplyCalculatedRange(indicator.ValueRaw, calculated.Value.Low, calculated.Value.High);
                            indicator.RefSource = RefSource.KbCalculated;
                            indicator.RefLowText = calculated.Value.Low.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            indicator.RefHighText = calculated.Value.High.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            changed = true;
                        }
                    }
                    // Ни диапазон, ни методика не дали результата под этого пациента — остаётся
                    // RefSource.None/Flag.Unknown, справочник просто не покрывает его случай
                    // (пол/возраст) — но привязка к статье (если она была нужна) уже применена выше.
                }
                // RefSource уже Blank/KbFixed/KbCalculated — он в приоритете и не переопределяется
                // справочником (см. IndicatorFlagCalculator.Calculate), трогаем только KbAnalyteId выше.

                if (changed) updated++;
            }
        }

        if (updated > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "RecalculateIndicatorFlagsJob: «{Name}» — обновлено {Updated} из {Total} показателей, ждавших справочник.",
            kb.DisplayName, updated, candidates.Count);
    }
}
