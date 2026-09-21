using FamilyHub.Api.Features.Push;
using FamilyHub.Api.Features.Notifications;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Security.Rotation;
using Hangfire;
using Hangfire.PostgreSql;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — Hangfire (сервер + 4 выделенных очереди) и
/// джобы оповещений/ротации, без изменения поведения/порядка.
/// </summary>
public static class BackgroundJobsRegistration
{
    public static WebApplicationBuilder AddFamilyHubBackgroundJobs(this WebApplicationBuilder builder)
    {
        // --- Оповещения: Hangfire recurring job по срокам годности лекарств и дням рождения (этап 3 п.10) ---
        var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Postgres.");
        builder.Services.AddHangfire(cfg => cfg.UsePostgreSqlStorage(opt => opt.UseNpgsqlConnection(postgresConnectionString)));
        builder.Services.AddHangfireServer(o => o.Queues = ["default"]);
        // Второй сервер, выделенная очередь "enrichment" (этап 4) с ОДНИМ воркером: обогащение
        // справочника не должно отъедать пропускную способность у ReminderScanJob/AuditRetentionJob,
        // а один воркер естественно укладывается в лимит внешнего поиска (Brave free-tier — 1 req/s),
        // без отдельного rate-limiter в коде (см. MedicationEnrichmentProcessor).
        builder.Services.AddHangfireServer(o =>
        {
            o.Queues = ["enrichment"];
            o.WorkerCount = 1;
            o.ServerName = "enrichment-server";
        });
        // Четвёртый сервер, выделенная очередь "extraction" (ветка medicalrecords) с ОДНИМ воркером —
        // та же причина, что у enrichment-server: LM Studio — один ноутбук за WireGuard, параллельных
        // запросов к нему быть не может физически, отдельная очередь просто не даёт распознаванию
        // анализов конкурировать за воркеров с ReminderScanJob/AuditRetentionJob/обогащением справочника.
        builder.Services.AddHangfireServer(o =>
        {
            o.Queues = ["extraction"];
            o.WorkerCount = 1;
            o.ServerName = "extraction-server";
        });
        // Третий сервер, выделенная очередь "rotation" (ADR-0009) с ОДНИМ воркером — та же причина, что
        // у enrichment-server: перешифровка данных при ротации ключа не должна отъедать пропускную
        // способность у остальных джоб. WorkerCount=1 попутно гарантирует, что одновременно исполняется
        // НЕ БОЛЕЕ ОДНОЙ EncryptionRotationJob — ручной клик "Перешифровать" из админки и тик ночного
        // добивателя просто встают в очередь друг за другом, конкурентной записи в
        // EncryptionRotationRun не возникает (см. EncryptionRotationJob, doc-комментарий).
        builder.Services.AddHangfireServer(o =>
        {
            o.Queues = ["rotation"];
            o.WorkerCount = 1;
            o.ServerName = "rotation-server";
        });
        // Пятый сервер, выделенная очередь "previews" (см. AttachmentPreviewProcessor) — WorkerCount=2,
        // не 1: в отличие от enrichment/extraction/rotation, генерация превью не упирается во внешний
        // ресурс с жёстким лимитом (Gotenberg — локальный сайдкар, не LM Studio за WireGuard и не
        // rate-limited поиск) — чистая CPU-задача (рендер PDF/картинок), два воркера дают параллелизм,
        // не конкурируя за упомянутые очереди.
        builder.Services.AddHangfireServer(o =>
        {
            o.Queues = ["previews"];
            o.WorkerCount = 2;
            o.ServerName = "previews-server";
        });
        builder.Services.AddScoped<EncryptionRotationJob>();
        builder.Services.AddScoped<ReminderScanJob>();
        builder.Services.AddScoped<NotificationService>();
        builder.Services.AddScoped<NotificationSendingService>();
        builder.Services.AddScoped<PushSubscriptionService>();

        return builder;
    }
}
