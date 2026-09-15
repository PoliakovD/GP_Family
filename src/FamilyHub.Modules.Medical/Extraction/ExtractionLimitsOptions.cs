namespace FamilyHub.Modules.Medical.Extraction;

/// <summary>Лимиты запуска конвейера распознавания на пользователя — секция "ExtractionLimits"
/// (не "Extraction" — та секция уже занята FamilyHub.Infrastructure.Documents.ExtractionOptions,
/// параметрами самого распознавания документа, не лимитами на пользователя; разные классы под
/// одной секцией конфигурации технически не конфликтуют, но две разные секции читаются понятнее).
/// Без них
/// один пользователь (батч-загрузка папки со сканами, см. record-batch-add) мог бы держать сколько
/// угодно параллельных задач `extraction` (дедуп — только частичный уникальный индекс по
/// MedicalRecordId, не по пользователю) и полностью занять единственный воркер LM Studio за
/// WireGuard (LmStudioConcurrencyGate — SemaphoreSlim(1,1) на процесс) на неопределённое время.
/// Все три числа конфигурируемы намеренно — интеграционные тесты поднимают их, тот же приём, что
/// AuthRateLimitOptions.</summary>
public class ExtractionLimitsOptions
{
    public const string SectionName = "ExtractionLimits";

    /// <summary>Сколько документов фронт разрешает застейджить в одной батч-загрузке (см.
    /// record-batch-add.component.ts) — мягкий барьер, настоящую защиту дают MaxActiveJobsPerUser/
    /// DailyJobsPerUser ниже и rate-лимит "llm" на POST /extract. Env: <c>ExtractionLimits__MaxBatchDocuments</c>.</summary>
    public int MaxBatchDocuments { get; set; } = 20;

    /// <summary>Сколько задач MedicalDocumentExtractionJob одновременно может быть у одного
    /// пользователя в статусе Pending/Running (ExtractionRequestService.RequestAsync) — мягкий
    /// лимит (две параллельные постановки могут обе пройти проверку до вставки строки), частоту
    /// повторных попыток ограничивает rate limiting отдельно, см. Program.cs, политика "llm".
    /// Env: <c>ExtractionLimits__MaxActiveJobsPerUser</c>.</summary>
    public int MaxActiveJobsPerUser { get; set; } = 20;

    /// <summary>Сколько задач извлечения пользователь может поставить в очередь за календарные
    /// сутки (UTC) — считается по CreatedAt в Postgres, переживает рестарт процесса (ADR-0001).
    /// Env: <c>ExtractionLimits__DailyJobsPerUser</c>.</summary>
    public int DailyJobsPerUser { get; set; } = 50;

    // --- Rate limiting (Program.cs, AddRateLimiter) — партиция по UserId (с фолбэком на IP для
    // редких неаутентифицированных путей), в отличие от AuthRateLimitOptions (партиция только по
    // IP, см. threat-model.md). Потолок "llm" обязан быть выше MaxBatchDocuments — батч честно шлёт
    // N POST /extract подряд, не должен словить 429 на собственном штатном сценарии.

    /// <summary>Политика "llm" — POST /extract, /summary/regenerate, /api/medications/ocr.
    /// Env: <c>ExtractionLimits__LlmPermitLimit</c>/<c>LlmWindowSeconds</c>.</summary>
    public int LlmPermitLimit { get; set; } = 40;
    public int LlmWindowSeconds { get; set; } = 60;

    /// <summary>Политика "medical-write" — POST /api/medical-records, POST .../attachments.
    /// Env: <c>ExtractionLimits__MedicalWritePermitLimit</c>/<c>MedicalWriteWindowSeconds</c>.</summary>
    public int MedicalWritePermitLimit { get; set; } = 60;
    public int MedicalWriteWindowSeconds { get; set; } = 60;
}
