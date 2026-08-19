using FamilyHub.Contracts.Events;
using FamilyHub.Contracts.Messaging;
using FamilyHub.TelegramBot.Configuration;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.TelegramBot.Messaging;

/// <summary>
/// Регистрация шины на стороне бота — НЕ FamilyHub.Infrastructure.Messaging.MassTransitRegistration.
/// AddFamilyHubMessaging: тот жёстко завязан на AddEntityFrameworkOutbox&lt;AppDbContext&gt;, а у
/// бота нет ни БД, ни AppDbContext. Бот только ЧИТАЕТ один топик (telegram-outbound) и делает
/// внешний вызов (Bot API) — транзакционный outbox ему не нужен по построению, поэтому это
/// отдельная (короткая) регистрация, а не общий метод с nullable-параметром DbContext.
/// </summary>
public static class BotMessagingRegistration
{
    public static IServiceCollection AddBotMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BotMessagingOptions>(configuration.GetSection(BotMessagingOptions.SectionName));
        var options = configuration.GetSection(BotMessagingOptions.SectionName).Get<BotMessagingOptions>() ?? new BotMessagingOptions();

        if (!options.Kafka.Enabled)
        {
            // Локальная разработка без брокера (ADR-0007, тот же идиом, что в Api) — бот
            // обслуживает только вебхук, без единого обращения к Kafka.
            services.AddMassTransit(x => x.UsingInMemory((_, _) => { }));
            return services;
        }

        // ADR-0007: TopicEndpoint-потребитель на ещё не существующий топик валит весь Kafka
        // Rider целиком при старте — создаём топик явно и синхронно ДО AddMassTransit (единственный
        // надёжный способ гарантировать порядок "топик существует → потребитель подписывается").
        KafkaTopicInitializer.EnsureTelegramOutboundTopicExists(options.Kafka.BootstrapServers);

        services.AddMassTransit(x =>
        {
            // Rider обязан висеть на bus instance; сам bus у бота пустой — он ничего не публикует
            // и не имеет ни одного receive endpoint на InMemory-стороне. Транзакционный outbox не
            // нужен по построению: бот только читает топик и делает внешний вызов.
            x.UsingInMemory((_, _) => { });

            x.AddRider(r =>
            {
                r.AddConsumer<TelegramOutboundConsumer>();

                r.UsingKafka((context, k) =>
                {
                    k.Host(options.Kafka.BootstrapServers);

                    k.TopicEndpoint<TelegramMessageRequestedEvent>(
                        KafkaTopics.TelegramOutbound, KafkaConsumerGroups.TelegramBotOutbound, e =>
                        {
                            e.UseMessageRetry(retry => retry.Exponential(
                                options.Retry.RetryLimit, options.Retry.MinInterval,
                                options.Retry.MaxInterval, options.Retry.IntervalDelta));
                            e.ConfigureConsumer<TelegramOutboundConsumer>(context);
                        });
                });
            });
        });

        return services;
    }
}
