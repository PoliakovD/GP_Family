using FamilyHub.Api.Configuration;
using FamilyHub.Api.Features.Auth;
using FamilyHub.Domain.Enums;
using FamilyHub.Infrastructure.Email.Templates;
using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Modules.Medical.Enrichment;

namespace FamilyHub.Api.Features.Dev;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — раньше три inline-лямбда эндпоинта жили
/// прямо в композиционном корне, единственные из ~24 групп маршрутов без собственного
/// Map*Endpoints-файла. По содержанию не изменены.
///
/// /dev/* — служебные эндпоинты без аутентификации по построению (ручной прогон Hangfire-джоб,
/// просмотр вёрстки писем). Гейт DevTools:DevEndpointsEnabled, независимо от AdminUiEnabled — на
/// VPS всегда false (см. деплой-план), локально включается вместе с DevAuthEnabled. Метод
/// самогейтится (проверяет флаг внутри), поэтому вызывается из Program.cs безусловно.
/// </summary>
public static class DevEndpoints
{
    public static void MapDevEndpoints(this IEndpointRouteBuilder app, DevToolsOptions devTools)
    {
        if (!devTools.DevEndpointsEnabled) return;

        // Ручной запуск джобы оповещений без ожидания cron/UI дашборда — для локальной проверки.
        // .DisableAntiforgery() — документирует явно (аудит, находка Medium #9), а не как случайный
        // побочный эффект того, что путь лежит вне /api: глобальный CSRF-гейт в Program.cs проверяет
        // мутирующие запросы только под /api, поэтому /dev/* и без атрибута фактически не защищён им
        // (app.UseAntiforgery() в этом приложении не подключён вовсе — см. AttachmentEndpoints/
        // MedicationOcrEndpoints, тот же паттерн). Риск невысок: DevEndpointsEnabled на VPS всегда
        // false (см. DevToolsOptions), но исключение должно быть видимым, а не случайным.
        app.MapPost("/dev/trigger-reminder-scan", async (ReminderScanJob job, CancellationToken ct) =>
        {
            await job.RunAsync(ct);
            return Results.Ok();
        }).DisableAntiforgery();

        // /dev/trigger-outbox-dispatch удалён (ADR-0006): у MassTransit нет поддерживаемого API
        // "прогнать доставку сейчас" — UseBusOutbox будит delivery service сразу после SaveChanges,
        // иначе полинг по Messaging:Outbox:QueryDelay. Тесты/дев — на QueryDelay + WaitForAsync-полинг.

        // Синхронный прогон конкретной задачи обогащения справочника (этап 4) — минуя очередь
        // Hangfire, для локальной проверки конвейера без ожидания воркера enrichment-server.
        // .DisableAntiforgery() — та же причина, что у /dev/trigger-reminder-scan выше.
        app.MapPost("/dev/trigger-enrichment/{jobId:guid}", async (
            Guid jobId, MedicationEnrichmentProcessor processor, CancellationToken ct) =>
        {
            await processor.RunAsync(jobId, ct);
            return Results.Ok();
        }).DisableAntiforgery();

        // Просмотр вёрстки email-писем в браузере: LoggingEmailSender печатает в лог только
        // текстовую часть, а SMTP в dev обычно не настроен, поэтому иначе HTML не увидеть без
        // EmailPreviewWriter (юнит-тест, пишущий файлы). Правка шаблона .html требует пересборки
        // API — они embedded-ресурсы (см. EmailTemplateRenderer). AllowAnonymous обязателен:
        // FallbackPolicy выше требует аутентификации, а у браузера при заходе сюда напрямую нет
        // ни Telegram initData, ни dev-заголовка (тот же случай, что и /hangfire выше).
        app.MapGet("/dev/email-preview/{name}", (string name, EmailTemplateRenderer renderer) =>
        {
            const string demoEmail = "demo@example.com";
            string? html = name switch
            {
                "temporary-password" => renderer.RenderTemporaryPassword(
                    TelegramBindingService.TemporaryPasswordCopy(demoEmail), demoEmail, "Kd7mQx4Ttb2z"),
                _ when Enum.TryParse<EmailCodePurpose>(name, ignoreCase: true, out var purpose) =>
                    renderer.RenderCode(EmailOtpService.CopyFor(purpose).Copy, "482915", 10),
                _ => null,
            };
            return html is null
                ? Results.NotFound("Доступные имена: register | linkemail | resetpassword | telegrambind | temporary-password")
                : Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();
    }
}
