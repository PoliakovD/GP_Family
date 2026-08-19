using System.Reflection;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FamilyHub.Contracts.Events;
using FamilyHub.Contracts.Messaging;
using FamilyHub.Infrastructure.Messaging.Kafka;
using FamilyHub.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyHub.Infrastructure.Messaging;

/// <summary>
/// Один потребитель на одно событие — событие в этом списке может маршрутизироваться сразу
/// нескольким потребителям (см. UserLeftFamilyEvent → Notifications + Medical cleanup), тогда у
/// него несколько записей с разными ConsumerGroup (иначе они конкурировали бы за партиции одного
/// топика вместо получения каждый своей копии сообщения — балансировка нагрузки Kafka, а не
/// fan-out, если group одна и та же).
/// </summary>
public record KafkaConsumerRegistration(Type EventType, Type ConsumerType, string ConsumerGroup);

/// <summary>
/// Композиционная точка входа шины (ADR-0006/ADR-0007, замена MediatR+собственного outbox).
/// Модули не видны отсюда (правило "модули не знают друг о друге") — сборки И конкретные типы
/// потребителей передаёт вызывающий (Program.cs — единственное место, которому позволено знать
/// обо всех модулях сразу), как и раньше делал AddMediatR.RegisterServicesFromAssemblies.
/// </summary>
public static class MassTransitRegistration
{
    public static IServiceCollection AddFamilyHubMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<KafkaConsumerRegistration> kafkaConsumers,
        params Assembly[] consumerAssemblies)
    {
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));
        var options = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>() ?? new MessagingOptions();

        services.AddScoped<IDomainEventPublisher, DomainEventPublisher>();

        // ADR-0007: TopicEndpoint-потребители подписываются на топики сразу при старте хоста — в
        // отличие от продюсера (которому auto.create.topics.enable создавал топик лениво при
        // первой публикации, см. ADR-0006 §5), подписка на ЕЩЁ НЕ существующий топик валит весь
        // Kafka Rider целиком ("ReceiveTransport faulted") — auto-create не успевает сработать до
        // первой попытки подписки. Создаём топики явно и синхронно ДО AddMassTransit — единственный
        // надёжный способ гарантировать порядок "топик существует → потребители подписываются".
        if (options.Kafka.Enabled)
            EnsureTopicsExist(options.Kafka.BootstrapServers);

        // Только для тестов (MessagingFailureIsolationTests + Kafka-аналог) — регистрирует
        // тестовый consumer-двойник без второго вызова AddMassTransit. Пусто в проде. Работает в
        // ОБЕИХ ветках Kafka.Enabled: InMemory — через AddConsumers(allAssemblies) ниже как
        // раньше; Kafka — реflection ищет в этих же сборках IConsumer<T> на известное событие и
        // подписывает на его топик со своей (авто-сгенерированной) consumer group, см. ниже.
        var extraAssemblies = new List<Assembly>();
        if (!string.IsNullOrWhiteSpace(options.ExtraConsumerAssemblies))
        {
            foreach (var name in options.ExtraConsumerAssemblies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                extraAssemblies.Add(Assembly.Load(name));
        }

        var allAssemblies = consumerAssemblies.Concat(extraAssemblies).ToList();

        if (extraAssemblies.Count > 0)
        {
            var extraKafkaConsumers = extraAssemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true })
                .SelectMany(t => t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
                    .Select(i => new KafkaConsumerRegistration(i.GetGenericArguments()[0], t, $"extra-{t.Name}")))
                .Where(r => KafkaTopics.ByEventType.ContainsKey(r.EventType));

            kafkaConsumers = kafkaConsumers.Concat(extraKafkaConsumers).ToList();
        }

        services.AddMassTransit(x =>
        {
            // ADR-0007: Kafka.Enabled переключает, ГДЕ живут бизнес-потребители — не "включает
            // внешнее зеркало поверх InMemory", как было в ADR-0006. false (dev-lite/юнит-тесты,
            // без Docker) — потребители на InMemory, сканом сборок, как и раньше. true
            // (docker-compose/прод) — потребители на Kafka Rider (см. ниже): InMemory тогда несёт
            // ровно одного потребителя, KafkaTopicBridgeConsumer<T> — локальный однострочный
            // relay outbox → Kafka, не транспорт для бизнес-логики (InMemory не пересекает границы
            // процесса, а бизнес-потребители обязаны пережить будущий вынос модуля в микросервис
            // без переписывания).
            if (!options.Kafka.Enabled)
                x.AddConsumers(allAssemblies.ToArray());

            x.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
                o.QueryDelay = options.Outbox.QueryDelay;
                o.QueryMessageLimit = options.Outbox.QueryMessageLimit;
                o.DuplicateDetectionWindow = options.Outbox.DuplicateDetectionWindow;
            });

            x.UsingInMemory((context, cfg) =>
            {
                cfg.UseMessageRetry(r => r.Exponential(
                    options.Retry.RetryLimit, options.Retry.MinInterval, options.Retry.MaxInterval, options.Retry.IntervalDelta));

                // ConfigureEndpoints создаёт receive endpoint на каждый ТИП, зарегистрированный
                // через x.AddConsumer(s) выше — в обеих ветках Enabled ими исчерпывается весь
                // список того, что должно жить на InMemory (Enabled=false — 7 бизнес-потребителей;
                // Enabled=true — только 6 KafkaTopicBridgeConsumer<T>, единственный получатель).
                cfg.ConfigureEndpoints(context);
            });

            if (options.Kafka.Enabled)
            {
                foreach (var eventType in DomainEventTypes.All)
                    x.AddConsumer(typeof(KafkaTopicBridgeConsumer<>).MakeGenericType(eventType));

                x.AddRider(r =>
                {
                    // Явные вызовы на каждый тип, не рефлексия — KafkaTopicsTests проверяет,
                    // что DomainEventTypes.All полностью покрыт KafkaTopics.ByEventType, так
                    // что рассинхронизация этого списка с константами не пройдёт незамеченной.
                    r.AddProducer<MedicalRecordSharedEvent>(KafkaTopics.MedicalRecordShared);
                    r.AddProducer<UserLeftFamilyEvent>(KafkaTopics.UserLeftFamily);
                    r.AddProducer<MemberApprovedEvent>(KafkaTopics.MemberApproved);
                    r.AddProducer<MedicationExpiringEvent>(KafkaTopics.MedicationExpiring);
                    r.AddProducer<BirthdayApproachingEvent>(KafkaTopics.BirthdayApproaching);
                    r.AddProducer<MedicationEnrichedEvent>(KafkaTopics.MedicationEnriched);
                    // Единственный топик, который Api публикует, но НЕ потребляет — читает его
                    // только FamilyHub.TelegramBot (см. TelegramOutboundPublisher/TelegramOutboundConsumer).
                    r.AddProducer<TelegramMessageRequestedEvent>(KafkaTopics.TelegramOutbound);

                    // Регистрация именно на r (реестр Rider'а), не на x (основная шина) — ниже
                    // ConfigureConsumer резолвит тип из IRiderRegistrationContext, отдельного от
                    // основного контейнера регистраций MassTransit; регистрация не в том реестре
                    // даёт "The consumer type was not found" при старте хоста (проверено эмпирически).
                    foreach (var registration in kafkaConsumers)
                        r.AddConsumer(registration.ConsumerType);

                    r.UsingKafka((context, k) =>
                    {
                        k.Host(options.Kafka.BootstrapServers);

                        // TEvent — известный на этапе компиляции тип из Contracts (Infrastructure
                        // уже на неё ссылается); ConsumerType — из другой сборки (Modules.*),
                        // которую Infrastructure сама не видит (правило "модули друг друга не
                        // знают") — поэтому дженерик закрывается здесь по TEvent явно (без
                        // рефлексии), а тип потребителя передаётся как Type в нерефлексийную
                        // перегрузку ConfigureConsumer(IRegistrationContext, Type).
                        void WireTopic<TEvent>(string topic) where TEvent : class
                        {
                            foreach (var registration in kafkaConsumers.Where(c => c.EventType == typeof(TEvent)))
                                k.TopicEndpoint<TEvent>(topic, registration.ConsumerGroup, e =>
                                {
                                    e.UseMessageRetry(retry => retry.Exponential(
                                        options.Retry.RetryLimit, options.Retry.MinInterval, options.Retry.MaxInterval, options.Retry.IntervalDelta));
                                    e.ConfigureConsumer(context, registration.ConsumerType);
                                });
                        }

                        WireTopic<MedicalRecordSharedEvent>(KafkaTopics.MedicalRecordShared);
                        WireTopic<UserLeftFamilyEvent>(KafkaTopics.UserLeftFamily);
                        WireTopic<MemberApprovedEvent>(KafkaTopics.MemberApproved);
                        WireTopic<MedicationExpiringEvent>(KafkaTopics.MedicationExpiring);
                        WireTopic<BirthdayApproachingEvent>(KafkaTopics.BirthdayApproaching);
                        WireTopic<MedicationEnrichedEvent>(KafkaTopics.MedicationEnriched);
                        // TelegramOutbound сюда намеренно не добавлен: у Api нет записи в
                        // kafkaConsumers для TelegramMessageRequestedEvent (WireTopic просто не
                        // найдёт совпадений в kafkaConsumers.Where(...) и не создаст endpoint) —
                        // топик читает только FamilyHub.TelegramBot, отдельным процессом/группой.
                    });
                });
            }
        });

        return services;
    }

    /// <summary>
    /// Синхронный (блокирующий, вызывается один раз при старте хоста, до AddMassTransit) вызов —
    /// см. комментарий на месте вызова. Идемпотентно: TopicAlreadyExists — не ошибка, обычный
    /// случай при рестарте процесса/нескольких инстансах. 152-ФЗ: retention.ms = 7 дней для всех
    /// топиков — тот же срок, что был у старого OutboxOptions.ProcessedRetention (ADR-0006 §5),
    /// часть событий несёт ПДн/медицинские данные (BirthdayApproachingEvent.PersonName и т.п.).
    /// </summary>
    private static void EnsureTopicsExist(string bootstrapServers)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrapServers }).Build();

        var specs = KafkaTopics.ByEventType.Values.Distinct().Select(topic => new TopicSpecification
        {
            Name = topic,
            NumPartitions = 1,
            ReplicationFactor = 1,
            Configs = new Dictionary<string, string>
            {
                ["retention.ms"] = ((long)TimeSpan.FromDays(7).TotalMilliseconds).ToString(),
            },
        }).ToList();

        try
        {
            admin.CreateTopicsAsync(specs).GetAwaiter().GetResult();
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
        {
            // Топики уже созданы предыдущим стартом этого же процесса/другим инстансом — норм.
        }
    }
}
