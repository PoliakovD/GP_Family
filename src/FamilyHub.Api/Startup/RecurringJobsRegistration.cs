using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Security.Rotation;
using FamilyHub.Modules.Medical.Extraction;
using Hangfire;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — регистрация рекуррентных Hangfire-джоб +
/// best-effort постановка задачи пересборки справочника при каждом старте, без изменения
/// поведения/порядка.
/// </summary>
public static class RecurringJobsRegistration
{
    public static WebApplication UseFamilyHubRecurringJobs(this WebApplication app)
    {
        // --- Регистрация ежедневной джобы оповещений (этап 3 п.10) ---
        // Через DI (IRecurringJobManager), а не статический RecurringJob.AddOrUpdate: последний
        // читает JobStorage.Current, который AddHangfire больше не выставляет автоматически.
        var notificationOptions = app.Services.GetRequiredService<IOptions<NotificationOptions>>().Value;
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<ReminderScanJob>(
            "reminder-scan",
            job => job.RunAsync(CancellationToken.None),
            notificationOptions.Cron);

        // Ретеншн аудита (задача 2.7): ежемесячно, 1-го числа в 03:00 — строки старше 12 месяцев.
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<FamilyHub.Infrastructure.Audit.AuditRetentionJob>(
            "audit-retention",
            job => job.RunAsync(CancellationToken.None),
            "0 3 1 * *");

        // Ночной добиватель ротации ключа (ADR-0009), ежедневно в 04:00: EncryptionRotationJob.RunAsync
        // сам по себе no-op, если нет строки EncryptionRotationRun в статусе Running — этот тик НИКОГДА
        // не запускает новый прогон (это делает только AdminKeysService по клику администратора), а лишь
        // резюмирует уже идущий, если предыдущее исполнение оборвалось (рестарт контейнера/редеплой,
        // транзиентный сбой сети до MinIO/Postgres) до того, как Hangfire исчерпал собственные ретраи
        // ([AutomaticRetry] на самом классе джобы).
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<EncryptionRotationJob>(
            "encryption-rotation-catchup",
            job => job.RunAsync(CancellationToken.None),
            "0 4 * * *");

        // Досып задач, ждущих ИИ (LM Studio недоступен: ноутбук выключен/спит) — каждую минуту проверяет
        // доступность и запускает ждавшие распознавания/резюме/биоматериалы, как только сервер снова
        // отвечает (см. класс-doc LmStudioRecoverySweepJob). Раз в минуту, а не в 5, — пользователь
        // видит «ждём ИИ» и не должен ждать лишние минуты после возвращения сервера.
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<LmStudioRecoverySweepJob>(
            "lmstudio-recovery-sweep",
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");

        // Напоминания о приёме лекарств (ADR-0015): раз в минуту — наступившие/повторные/пропущенные приёмы,
        // раз в час — запас в аптечке, автозавершение курсов и чистка токенов push-кнопок.
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<FamilyHub.Modules.Medical.MedicationCourses.MedicationDoseScanJob>(
            "medication-dose-scan",
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");
        app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<FamilyHub.Modules.Medical.MedicationCourses.MedicationCourseMaintenanceJob>(
            "medication-course-maintenance",
            job => job.RunAsync(CancellationToken.None),
            "0 * * * *");

        return app;
    }

    /// <summary>Пересборка enrich-пайплайна анализов: один батч принудительного переобогащения
    /// справочника показателей на каждый старт API (LabAnalyteKbReenrichJob) — идемпотентно
    /// (no-op, если строк со старой схемой не осталось) и самовосстановимо: если предыдущий батч не
    /// успел закрыть весь справочник, следующий деплой продолжит его сам, без ручного клика в
    /// админке. Ручной повторный запуск всё равно доступен через POST
    /// /api/admin/kb/lab-analytes/reenrich (см. AdminEndpoints). Постановка — best-effort:
    /// недоступность Hangfire-стораж на старте не должна ронять хост целиком (тот же принцип, что
    /// EnrichmentRequestService/LabAnalyteEnrichmentRequestService — см. их доки).</summary>
    public static WebApplication EnqueueStartupMaintenanceJobs(this WebApplication app)
    {
        try
        {
            app.Services.GetRequiredService<IBackgroundJobClient>().Enqueue<LabAnalyteKbReenrichJob>(
                j => j.RunAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            app.Services.GetRequiredService<ILogger<Program>>().LogWarning(
                ex, "Не удалось поставить LabAnalyteKbReenrichJob в очередь при старте — попробуется на следующем деплое.");
        }

        return app;
    }
}
