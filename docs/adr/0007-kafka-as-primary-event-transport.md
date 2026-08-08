# ADR-0007. Kafka Rider — реальный транспорт для бизнес-потребителей событий (пересмотр части ADR-0006)

**Статус:** принят. **Дата:** 2026-08-08.

## Контекст

[ADR-0006](0006-event-bus-masstransit-and-kafka.md) заменил MediatR на MassTransit 8.5.1 + EF Core
Outbox, добавив Kafka Rider как **внешний фан-аут-мост**: `IDomainEventPublisher.PublishAsync` →
EF Outbox → InMemory-шина → (а) 7 бизнес-потребителей (Notifications, Medical cleanup) на той же
InMemory-шине **и** (б) `KafkaTopicBridgeConsumer<T>` — тоже на InMemory — зеркалящий событие в
Kafka для внешних потребителей.

Дальнейший разбор вскрыл структурную дыру в этой схеме: бизнес-потребители, сидя на InMemory,
физически не переживают вынос своего модуля в отдельный процесс — InMemory не пересекает границы
процесса. В день, когда `Modules.Medical` уехал бы в свой контейнер,
`UserLeftFamilyMedicalCleanupConsumer` пришлось бы **переписывать** на Kafka-подписку. То есть
обещание ADR-0006 "смена транспорта без переписывания потребителей" было верно только для
внешнего зеркала, не для внутренней доставки — ровно того места, которое имеет значение при
реальном выносе модуля.

Дополнительно подтверждено (MassTransit issues/discussions): транзакционный EF Core Outbox
напрямую на `ITopicProducer<T>` Kafka Rider поддерживается только с версии v9.1+ (коммерческая,
не Apache-2.0) — в используемой 8.5.1 такой интеграции нет. Поэтому локальный InMemory-мост
(`KafkaTopicBridgeConsumer<T>`) остаётся необходим как единственный способ сохранить
транзакционность публикации (см. Решение п.1) — но его роль сужается: только этот однострочный
технический перенос, не транспорт для бизнес-логики.

## Решение

1. **7 бизнес-потребителей переезжают на Kafka Rider (`TopicEndpoint`)**, а не на InMemory.
   `MassTransitRegistration.cs`: при `Messaging:Kafka:Enabled=true` регистрация ветвится —
   `KafkaTopicBridgeConsumer<T>` (6 штук, по одному на событие) остаётся единственным получателем
   на InMemory (локальный relay `outbox → тот же процесс → Kafka`), а сами бизнес-потребители
   подписываются на реальные Kafka-топики через явные `k.TopicEndpoint<TEvent>(topic, group, ...)`.
   Список пар событие/потребитель/consumer-group — `KafkaConsumerRegistration`, передаётся из
   `Program.cs` (единственное место, знающее конкретные типы потребителей из всех модулей сразу —
   Infrastructure по правилу "модули друг друга не знают" эти типы не видит).

   | Событие | Топик | Потребитель | Consumer group |
   |---|---|---|---|
   | `MedicalRecordSharedEvent` | `medical-record-shared` | `MedicalRecordSharedNotificationConsumer` | `notifications-medical-record-shared` |
   | `UserLeftFamilyEvent` | `user-left-family` | `UserLeftFamilyNotificationConsumer` | `notifications-user-left-family` |
   | `UserLeftFamilyEvent` | `user-left-family` | `UserLeftFamilyMedicalCleanupConsumer` | `medical-user-left-family` |
   | `MemberApprovedEvent` | `member-approved` | `MemberApprovedNotificationConsumer` | `notifications-member-approved` |
   | `MedicationExpiringEvent` | `medication-expiring` | `MedicationExpiringNotificationConsumer` | `notifications-medication-expiring` |
   | `BirthdayApproachingEvent` | `birthday-approaching` | `BirthdayApproachingNotificationConsumer` | `notifications-birthday-approaching` |
   | `MedicationEnrichedEvent` | `medication-enriched` | `MedicationEnrichedNotificationConsumer` | `notifications-medication-enriched` |

   `UserLeftFamilyEvent` — единственное событие с двумя потребителями — получает две РАЗНЫЕ
   consumer group на одном топике: иначе они конкурировали бы за партиции (балансировка нагрузки
   Kafka), а не оба получали бы копию каждого сообщения (fan-out).

2. **Потребители не переписываются.** `IConsumer<T>.Consume(ConsumeContext<T>)` — одна и та же
   абстракция что на InMemory, что на Rider. Меняется только регистрация, ноль изменений в
   `Consumers/*.cs`.

3. **`Messaging:Kafka:Enabled` меняет смысл.** Раньше — "включить опциональное внешнее зеркало
   поверх работающей InMemory-доставки". Теперь — переключатель, ГДЕ живут бизнес-потребители:
   `true` (docker-compose/прод, дефолт для полного стека) — Kafka Rider, единственный реальный
   путь доставки; `false` (дефолт `appsettings.json`, юнит-тесты, casual IDE-запуск) —
   dev-lite-режим, потребители на InMemory, как было до этого ADR. Не "опциональность Kafka
   вообще" — а выбор между прод-топологией и лёгким dev-режимом без брокера.

4. **Топики создаются явно при старте хоста, не полагаясь на `auto.create.topics.enable`.**
   Обнаружено эмпирически: `TopicEndpoint`-потребитель, подписывающийся на ещё не существующий
   топик, валит весь Kafka Rider целиком (`KafkaConnectionException: ReceiveTransport faulted`) —
   auto-create не успевает сработать до первой попытки подписки (в отличие от продюсера, которому
   ленивое создание при первой публикации подходило, см. ADR-0006 §5). `MassTransitRegistration.
   EnsureTopicsExist` — синхронный `Confluent.Kafka.Admin.IAdminClient.CreateTopicsAsync` для всех
   `KafkaTopics.ByEventType.Values` до `AddMassTransit`, идемпотентно (`TopicAlreadyExists` — не
   ошибка), `retention.ms` = 7 дней (152-ФЗ, тот же срок, что был у `OutboxOptions.ProcessedRetention`).

5. **Явное разделение "события → Kafka, команды → Hangfire" — именованный принцип проекта.**
   Hangfire (`ReminderScanJob`, `MedicationEnrichmentProcessor`, OCR-конвейер) уже был правильно
   отделён как императивная/job-работа — этот ADR ничего в нём не меняет, только фиксирует
   разделение явно: записываемые/бизнес-события (факт, о котором могут быть заинтересованы
   несколько независимых потребителей, в т.ч. будущие внешние) идут через `IDomainEventPublisher`
   → Kafka; императивная команда "сделай X" (без множественных заинтересованных наблюдателей) —
   через Hangfire `BackgroundJob`/recurring job.

6. **`Messaging:ExtraConsumerAssemblies`-seam расширен на Kafka-ветку.** Раньше работал только для
   InMemory (`AddConsumers` сканил доп. сборки). Теперь `MassTransitRegistration` дополнительно
   рефлексией находит в этих же сборках `IConsumer<T>` на известные события и подписывает на
   соответствующий топик с авто-сгенерированной consumer group (`extra-{TypeName}`) — тот же
   тестовый seam (`MessagingFailureIsolationTests`/`KafkaMessagingFailureIsolationTests`) работает
   в обеих топологиях без дублирования механизма.

## Последствия

- **Юнит-тесты не проверяют Kafka-топологию напрямую.** Подтверждено (MassTransit discussions):
  у Kafka Rider нет in-memory тестового харнесса ("there is no in-memory rider implementation for
  unit testing") — `DomainEventTestPipeline`/`ConsumerFailureIsolationTests` всегда собирают
  потребителей по InMemory-ветке (`Enabled=false`), проверяя бизнес-логику потребителей в
  изоляции, не реальные топики/consumer group. Ответственность за прод-топологию — целиком у
  `KafkaIntegrationCollection` (`KafkaBridgeFlowTests`, `KafkaMessagingFailureIsolationTests`,
  `Testcontainers.Kafka`): подписка правильных потребителей на правильные топики, независимость
  двух consumer group одного события (`UserLeftFamilyEvent`), изоляция сбоя одной группы от
  соседней. `FamilyHubWebFactory`-семейство (~130 остальных тестов) намеренно не переведено на
  Kafka — `Enabled=false` там достаточен для проверки не-событийного функционала без лишней
  стоимости Docker-контейнера на каждую из ~15 коллекций.
- **Явное создание топиков при старте — новая обязанность composition root.** Без брокера,
  доступного к моменту старта хоста (`Messaging:Kafka:Enabled=true`), приложение не поднимется —
  `docker-compose.yml` уже гарантирует это через `depends_on: kafka: condition: service_healthy`.
- **При выносе модуля в микросервис Kafka-потребители переезжают без изменения кода** — они и
  сегодня, живя в монолите, подписаны на реальный Kafka-топик через `TopicEndpoint`, а не на
  InMemory; меняется только `.csproj`/деплой того сервиса, не регистрация потребителя.
- Регресс, унаследованный от ADR-0006, не отменяется этим ADR: ретраи Kafka-потребителя
  (`Messaging:Retry:*`, per-`TopicEndpoint`) по-прежнему не переживают рестарт процесса — та же
  идемпотентность потребителей (`DedupKey`+UNIQUE, `ExecuteDeleteAsync`) остаётся страховкой.

## Связанные решения

- [ADR-0006](0006-event-bus-masstransit-and-kafka.md) — базовая замена MediatR на MassTransit + EF Core Outbox + Kafka Rider (частично пересмотрен этим ADR).
- [ADR-0001](0001-data-locality-and-egress.md) — РФ-контур; топики создаются с явным `retention.ms`, брокер только self-hosted.
