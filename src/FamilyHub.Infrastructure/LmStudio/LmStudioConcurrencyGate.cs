namespace FamilyHub.Infrastructure.LmStudio;

/// <summary>
/// Процесс-широкий семафор на единственный вызов LM Studio одновременно (аудит, находка High #2).
/// LM Studio — один ноутбук за WireGuard, параллельных запросов к нему быть не может физически —
/// эта дисциплина уже соблюдалась для фонового конвейера через Hangfire-очереди с WorkerCount=1
/// ("extraction"/"enrichment"), но HTTP-эндпоинт синхронного OCR (POST /api/medications/ocr)
/// шёл в LmStudioJsonClient напрямую, без какой-либо сериализации — два пользователя,
/// сфотографировавших упаковку одновременно, отправляли параллельные запросы к одному инстансу
/// модели. Регистрируется singleton и используется единственной точкой входа — LmStudioJsonClient
/// — поэтому защищает сразу все пути (OCR, извлечение анализов, суммаризация обогащения), не
/// только OCR: применение семафора к уже сериализованным Hangfire-путям — не более чем лишний,
/// почти всегда невостребованный WaitAsync поверх дисциплины, которая и так гарантирует не более
/// одного одновременного воркера на очередь.
/// </summary>
public sealed class LmStudioConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public Task WaitAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
