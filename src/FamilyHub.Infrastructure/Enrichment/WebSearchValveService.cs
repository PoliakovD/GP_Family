using FamilyHub.Domain.Entities;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.Enrichment;

/// <summary>
/// Глобальный вентиль платного веб-поиска (замена месячной квоты, ADR-0005 §9) — единственная
/// строка WebSearchConfig, отсутствие строки означает "открыт" (та же конвенция, что
/// PipelineStepConfig/LmStudioReasoningConfig). Намеренно БЕЗ кеша, в отличие от
/// LmStudioModelProvider/PipelineConfigService: это ровно та проверка, что стоит прямо перед
/// вызовом платного внешнего API (5 ₽ за штуку) — пятиминутный TTL здесь означал бы до пяти минут
/// платных вызовов ПОСЛЕ того, как админ нажал «пауза», то есть ровно тот сценарий, ради которого
/// вентиль вводится. Один индексированный SELECT по одной строке — цена этой честности пренебрежимо
/// мала по сравнению с ценой одного платного вызова, который вентиль должен был остановить.
/// </summary>
public class WebSearchValveService(AppDbContext db) : IWebSearchValveService
{
    public async Task<bool> IsPausedAsync(CancellationToken ct = default)
    {
        var row = await db.WebSearchConfigs.AsNoTracking().FirstOrDefaultAsync(ct);
        return row?.IsPaused ?? false;
    }

    public async Task SetPausedAsync(bool paused, string? note, CancellationToken ct = default)
    {
        var row = await db.WebSearchConfigs.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new WebSearchConfig { Id = Guid.NewGuid() };
            db.WebSearchConfigs.Add(row);
        }

        row.IsPaused = paused;
        row.PausedAt = paused ? DateTime.UtcNow : null;
        row.Note = note;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
