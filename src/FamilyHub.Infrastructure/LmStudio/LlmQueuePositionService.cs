using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Считает, сколько задач реально стоят раньше данной в очереди к ЕДИНСТВЕННОМУ локальному LLM —
/// не в своей таблице задач, а по ВСЕМ четырём (MedicalDocumentExtractionJob, LabAnalyteEnrichmentJob,
/// MedicationEnrichmentJob, VisitMedicationEnrichmentJob). "extraction" и "enrichment" — РАЗНЫЕ
/// Hangfire-серверы с собственными воркерами (см. Program.cs) — задача из каждой очереди может
/// дойти до Status=Running независимо и одновременно; реально говорить с моделью в любой момент
/// может только ОДНА (см. LmStudioConcurrencyGate, SemaphoreSlim(1,1)) — Running здесь означает
/// "Hangfire уже взял задачу в работу", а не "модель прямо сейчас отвечает по этой задаче". Без
/// этого счётчика позиция внутри одной таблицы (см. прежний ExtractionQueryService.GetStatusAsync)
/// систематически недооценивает реальное ожидание, когда параллельно идёт большой поток задач
/// обогащения справочника — ровно то, что путает пользователя: кнопка «Распознать» блокирована
/// (это правильно, задача Running), а бейдж стадии ("Читаем текст"/"OCR") создаёт впечатление
/// активной работы, хотя задача на самом деле просто ждёт своей очереди у семафора.
///
/// Аппроксимация по CreatedAt, не точный порядок выдачи семафора — SemaphoreSlim не гарантирует
/// строгий FIFO, но для человекочитаемой позиции в очереди этого достаточно (тот же принцип, что
/// уже применялся к позиции внутри одной таблицы).
/// </summary>
public class LlmQueuePositionService(AppDbContext db)
{
    /// <summary>Позиция ОДНОЙ задачи — для случаев, где нужен только один результат (см.
    /// ExtractionQueryService.GetStatusAsync). Для страницы с МНОГИМИ задачами (список
    /// показателей/медикаментов) используйте GetActiveJobTimestampsAsync + CountAhead — по одному
    /// массовому запросу на все 4 таблицы вместо N+1 отдельных вызовов этого метода.</summary>
    public async Task<int> GetQueueAheadAsync(DateTime createdAt, CancellationToken ct = default)
    {
        var timestamps = await GetActiveJobTimestampsAsync(ct);
        return CountAhead(timestamps, createdAt);
    }

    /// <summary>Время создания ВСЕХ активных (Pending/Running) задач по всем четырём таблицам —
    /// один поход в БД (4 лёгких запроса без данных, только CreatedAt), дальше позиция для любого
    /// количества задач считается в памяти (CountAhead), без дополнительных SQL-запросов на
    /// каждую строку страницы. Статусы сравниваются inline в каждом Where — приватный метод-предикат
    /// (как было раньше) EF Core не может транслировать в SQL, только в клиентское вычисление.</summary>
    public async Task<List<DateTime>> GetActiveJobTimestampsAsync(CancellationToken ct = default)
    {
        var extraction = await db.MedicalDocumentExtractionJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running)
            .Select(j => j.CreatedAt).ToListAsync(ct);
        var labAnalyte = await db.LabAnalyteEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running)
            .Select(j => j.CreatedAt).ToListAsync(ct);
        var medication = await db.MedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running)
            .Select(j => j.CreatedAt).ToListAsync(ct);
        var visitMedication = await db.VisitMedicationEnrichmentJobs.AsNoTracking()
            .Where(j => j.Status == EnrichmentJobStatus.Pending || j.Status == EnrichmentJobStatus.Running)
            .Select(j => j.CreatedAt).ToListAsync(ct);

        extraction.AddRange(labAnalyte);
        extraction.AddRange(medication);
        extraction.AddRange(visitMedication);
        return extraction;
    }

    /// <summary>Сколько временных меток строго раньше createdAt — та же задача сама себя не
    /// считает (её собственная метка не строго меньше себя же).</summary>
    public static int CountAhead(List<DateTime> activeJobTimestamps, DateTime createdAt) =>
        activeJobTimestamps.Count(t => t < createdAt);
}
