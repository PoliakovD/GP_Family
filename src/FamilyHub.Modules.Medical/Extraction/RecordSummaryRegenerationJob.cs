using System.Text.Json;
using FamilyHub.Infrastructure.Persistence;
using FamilyHub.Modules.Medical.Pipeline;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>
/// Фоновый пересчёт «Резюме»/«Вопросов врачу» после ручной правки записи (добавили/изменили/
/// удалили показатель, сменили источник) — вместо прежней кнопки «Пересчитать резюме».
///
/// Дебаунс: каждая правка ставит <see cref="MedicalRecord.SummaryDirtyAt"/> и планирует эту джобу с
/// задержкой <see cref="Delay"/>, передавая токен-версию (<c>dirtyTicks</c>). Серия быстрых правок
/// подряд планирует несколько джоб, но пересчитывает только последняя: остальные видят чужой токен
/// и молча выходят. Токен сверяется дважды — на входе и после (долгого) вызова LLM, чтобы результат
/// по устаревшему набору показателей не перетёр более свежую правку.
///
/// Неудача суммаризатора (LM Studio недоступен, гейт отклонил ответ) сбрасывает резюме в null, а не
/// оставляет старое: старое построено на значениях, которых уже нет в записи, а для медицинского
/// текста устаревшее опаснее отсутствующего. Фронт при открытии «Резюме» без готового текста сам
/// один раз просит синхронный пересчёт (POST .../summary/regenerate).
/// </summary>
[Queue("extraction")]
public class RecordSummaryRegenerationJob(
    AppDbContext db,
    LabSummarizer summarizer,
    IPipelineConfigService pipelineConfig,
    ILogger<RecordSummaryRegenerationJob> logger)
{
    /// <summary>Сколько ждём после правки, прежде чем звать LLM — пользователь обычно правит
    /// несколько показателей подряд, пересчёт после каждого был бы пустой тратой.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(15);

    /// <summary>Токен-версия для <see cref="MedicalRecord.SummaryDirtyAt"/>: время с точностью до
    /// микросекунды, потому что Postgres хранит timestamptz именно так — иначе токен, прошедший
    /// круг через БД, не совпал бы с переданным в джобу (тики .NET — 100 нс).</summary>
    public static DateTime NewDirtyToken()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
    }

    public async Task RunAsync(Guid recordId, long dirtyTicks, CancellationToken ct = default)
    {
        var record = await db.MedicalRecords.FirstOrDefaultAsync(r => r.Id == recordId, ct);
        if (record is null || record.SummaryDirtyAt?.Ticks != dirtyTicks) return;

        var indicators = await db.LabIndicators.AsNoTracking().Where(i => i.MedicalRecordId == recordId).ToListAsync(ct);

        string? summaryJson = null;
        var enabled = await pipelineConfig.IsEnabledAsync(PipelineCatalog.AnalysisExtraction, "record-summary", ct);
        if (indicators.Count > 0 && enabled)
        {
            var summarized = await summarizer.SummarizeAsync(indicators, ct);
            if (summarized.Success && summarized.Summary is not null)
                summaryJson = JsonSerializer.Serialize(summarized.Summary);
            else
                logger.LogWarning("Автопересчёт резюме записи {RecordId} не удался — резюме сброшено.", recordId);
        }

        // LLM-вызов долгий — пока он шёл, правка могла повториться. Тогда токен уже другой, и
        // результат по старому набору показателей записывать нельзя (свежая джоба уже в пути).
        var currentToken = await db.MedicalRecords.AsNoTracking()
            .Where(r => r.Id == recordId).Select(r => r.SummaryDirtyAt).FirstOrDefaultAsync(ct);
        if (currentToken?.Ticks != dirtyTicks) return;

        record.SummaryJson = summaryJson;
        record.SummaryDirtyAt = null;
        await db.SaveChangesAsync(ct);
    }
}
