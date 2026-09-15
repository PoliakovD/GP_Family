using System.Globalization;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Одноразовый перепрогон (план "нормы из бланка: односторонние референсы и качественные
/// результаты") — показатели, распознанные ДО фикса каскада IndicatorFlagCalculator, застряли на
/// Flag.Unknown навсегда: RecalculateIndicatorFlagsJob пересчитывает только записи, ждущие
/// справочник (RefSource.None/Inferred), а показатель с "&lt;47" в RefText или "не обнаружено" без
/// референса сидел на RefSource.Blank — Blank в приоритете каскада и никогда не переопределяется,
/// значит и не подхватывался этой очередью повторных попыток. Здесь — не тот каскад, а прямой
/// повторный прогон IndicatorFlagCalculator.Calculate по уже сохранённым ValueRaw/RefText/
/// RefLowText/RefHighText: то, что раньше не распарсилось (ReferenceRangeTextParser/
/// QualitativeResultClassifier не существовали), теперь распарсится корректно.
///
/// Однократный ручной триггер (POST /api/admin/pipeline/recompute-indicator-flags), не часть
/// обычного конвейера — новые записи чинятся самим фиксом на этапе распознавания, старые лечит
/// этот прогон один раз.
///
/// Плюс QualitativeNormJudge (см. class doc) — тот же последний резервный шаг, что в
/// MedicalDocumentExtractionProcessor, только без dto.RefExpected (никогда не персистится, у
/// старых записей его и не было): судит по названию/значению/пояснению справочника, если
/// показатель уже привязан к KB-записи.
/// </summary>
[Queue("enrichment")]
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 600, 3600])]
public class RecomputeIndicatorFlagsBackfillJob(
    AppDbContext db, QualitativeNormJudge qualitativeJudge, IPipelineConfigService pipelineConfig,
    ILogger<RecomputeIndicatorFlagsBackfillJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var stuck = await db.LabIndicators.Where(i => i.Flag == IndicatorFlag.Unknown).ToListAsync(ct);
        if (stuck.Count == 0)
        {
            logger.LogInformation("RecomputeIndicatorFlagsBackfillJob: застрявших показателей не найдено.");
            return;
        }

        var updated = 0;
        foreach (var group in stuck.GroupBy(i => i.MedicalRecordId))
        {
            var record = await db.MedicalRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == group.Key, ct);
            if (record is null) continue;

            var (ageYears, sex) = await PatientIdentityResolver.ResolveAsync(db, record, ct);

            foreach (var indicator in group)
            {
                // RefExpected никогда не персистится (только этап распознавания знает о нём) —
                // у старых записей его и не было, воссоздать нечем; TryApplyInferred здесь не
                // вызывается вовсе (ему нечего разбирать), но QualitativeNormJudge ниже всё равно
                // может помочь по названию/значению/пояснению справочника.
                var dto = new ExtractedLabIndicator(
                    indicator.DisplayName, indicator.ValueRaw, indicator.Unit,
                    ParseDouble(indicator.RefLowText), ParseDouble(indicator.RefHighText), indicator.RefText);

                KbReferenceRange? kbFallback = null;
                LabAnalyteKbPayload.KbNormExplanations? kbNorm = null;
                if (indicator.KbAnalyteId is not null)
                {
                    var kb = await db.GlobalLabAnalytesKb.AsNoTracking()
                        .FirstOrDefaultAsync(k => k.Id == indicator.KbAnalyteId, ct);
                    if (kb is not null)
                    {
                        kbFallback = IndicatorFlagCalculator.PickBestRange(
                            LabAnalyteKbPayload.ParseRefRanges(kb.PayloadJson), ageYears, sex);
                        kbNorm = LabAnalyteKbPayload.ParseNormExplanations(kb.PayloadJson);
                    }
                }

                var (flag, refSource, effLow, effHigh) = IndicatorFlagCalculator.Calculate(dto, kbFallback, ageYears, sex);

                // Последний резервный шаг (см. class doc) — только когда деterministic-каскад
                // выше не дал вообще ничего; RefExpected здесь всегда null (никогда не
                // персистится), но границы диапазона (свои или из KB) и пояснения справочника —
                // уже полезный контекст сами по себе (живой случай: отсутствие — норма, хотя
                // диапазон в справочнике начинается не с нуля, например "2-10" — см. LowMeans).
                if (flag == IndicatorFlag.Unknown &&
                    await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "qualitative-judge", ct))
                {
                    var isNormal = await qualitativeJudge.JudgeAsync(
                        indicator.DisplayName, indicator.ValueRaw, indicator.Unit, modelExpectedNorm: null,
                        effLow ?? kbFallback?.Low, effHigh ?? kbFallback?.High, kbNorm, ct);
                    if (isNormal is not null)
                    {
                        flag = isNormal.Value ? IndicatorFlag.Normal : IndicatorFlag.High;
                        refSource = RefSource.Inferred;
                    }
                }

                if (flag == IndicatorFlag.Unknown) continue;

                indicator.Flag = flag;
                indicator.RefSource = refSource;
                indicator.RefLowText = effLow?.ToString(CultureInfo.InvariantCulture);
                indicator.RefHighText = effHigh?.ToString(CultureInfo.InvariantCulture);
                updated++;
            }
        }

        if (updated > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "RecomputeIndicatorFlagsBackfillJob: пересчитано {Updated} из {Total} застрявших показателей.",
            updated, stuck.Count);
    }

    private static double? ParseDouble(string? value) =>
        value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
}
