using FamilyHub.Infrastructure.Notifications;
using FamilyHub.Infrastructure.Telegram;
using Microsoft.Extensions.Options;

namespace FamilyHub.Api.Startup;

/// <summary>
/// Извлечено из Program.cs при cleanup-рефакторинге — Telegram-доставка через шину, внутренний
/// API для FamilyHub.TelegramBot и Web Push, без изменения поведения/порядка.
/// </summary>
public static class NotificationChannelsRegistration
{
    /// <returns>TelegramBotConfigured/InternalBotApiConfigured — нужны позже, после app.Build(),
    /// для условного маппинга /internal/bot/* эндпоинтов (см. Program.cs).</returns>
    public static (bool TelegramBotConfigured, bool InternalBotApiConfigured)
        AddFamilyHubNotificationChannels(this WebApplicationBuilder builder)
    {
        // --- Telegram: доставка оповещений через шину (этап 4 п.12; бот сам живёт в отдельном ---
        // --- процессе FamilyHub.TelegramBot, см. ADR-0008) ---
        // BotToken по-прежнему обязателен здесь: TelegramInitDataValidator выводит из него HMAC-ключ
        // для проверки initData Mini App — это забота Api, а не бота. Сам бот (вебхук, SendMessage)
        // не живёт в этом процессе больше; TelegramOutboundPublisher публикует готовое сообщение в
        // Kafka (topic telegram-outbound), которое потребляет FamilyHub.TelegramBot.
        var telegramBotToken = builder.Configuration["Telegram:BotToken"];
        var telegramBotConfigured = !string.IsNullOrWhiteSpace(telegramBotToken);
        if (telegramBotConfigured)
        {
            builder.Services.AddScoped<INotificationSender, TelegramOutboundPublisher>();
        }

        // --- Внутренний API для FamilyHub.TelegramBot (/internal/bot/*, см. InternalBotEndpoints) ---
        // Отдельный флаг от telegramBotConfigured: этот секрет защищает контур обмена с ботом-процессом,
        // а не с Telegram напрямую, и может быть сконфигурирован независимо (напр. в проде — всегда,
        // в локальной разработке без контейнера бота — не обязателен).
        var internalBotApiToken = builder.Configuration["Internal:BotApiToken"];
        var internalBotApiConfigured = !string.IsNullOrWhiteSpace(internalBotApiToken);
        if (internalBotApiConfigured && internalBotApiToken!.Length < 32)
            throw new InvalidOperationException(
                "Internal:BotApiToken (env Internal__BotApiToken) короче 32 символов — секрет обмена с " +
                "FamilyHub.TelegramBot слишком слабый. Сгенерировать: `openssl rand -hex 32`.");

        // --- Web Push: реальная доставка PWA-пользователям (редизайн навигации, ADR-0004) — покрывает
        // пользователей без Telegram, которых TelegramNotificationSender не видит вовсе. Независимо от
        // Telegram-канала: оба могут быть настроены одновременно (NotificationSendingService.TrySendAsync
        // фан-аутит на ВСЕ зарегистрированные INotificationSender, см. IEnumerable<INotificationSender>).
        var webPushOptions = builder.Configuration.GetSection(WebPushOptions.SectionName).Get<WebPushOptions>();
        var webPushConfigured = webPushOptions?.IsConfigured == true;
        if (webPushConfigured)
        {
            builder.Services.AddSingleton<WebPush.IWebPushClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<WebPushOptions>>().Value;
                var client = new WebPush.WebPushClient();
                client.SetVapidDetails(options.Subject, options.VapidPublicKey, options.VapidPrivateKey);
                return client;
            });
            builder.Services.AddScoped<INotificationSender, WebPushNotificationSender>();
        }

        // Ни один реальный канал не настроен (типично — локальный dev) — доставка остаётся в логах.
        if (!telegramBotConfigured && !webPushConfigured)
        {
            builder.Services.AddScoped<INotificationSender, LoggingNotificationSender>();
        }

        return (telegramBotConfigured, internalBotApiConfigured);
    }
}
