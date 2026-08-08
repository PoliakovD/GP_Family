# ADR-0006. Событийная шина: MediatR → MassTransit 8.5.1 + Kafka Rider

**Статус:** принят, частично пересмотрен [ADR-0007](0007-kafka-as-primary-event-transport.md) в
части внутренней доставки бизнес-событий (Kafka Rider вместо InMemory для потребителей). **Дата:** 2026-08-07.

## Контекст

Событийная шина до этого решения — MediatR 12.5.0 (только `INotification`/`IPublisher`, ни
одного `IRequest` во всём решении) поверх собственного Postgres-outbox
(`src/FamilyHub.Infrastructure/Outbox/`): `IOutboxWriter.Enqueue` писал строку в тот же
`AppDbContext`/транзакцию, что и бизнес-данные; `OutboxDispatcher` (BackgroundService) полил
таблицу и вызывал `IPublisher.Publish`; `IsolatingLoggingPublisher` — кастомный
`INotificationPublisher`, прогонявший все хендлеры даже при падении одного, агрегировавший
ошибки в `AggregateException`, из-за чего ретраилась вся строка целиком (at-least-once на
уровне события, не хендлера).

Это работало, но у решения было два практических предела:

1. **Лицензия.** MediatR с v13 (апрель 2025) стала коммерческой — 12.5.0 оставалась последней
   Apache-2.0 версией без гарантии дальнейших патчей. У MassTransit та же история: v9 (тоже
   апрель 2025) тоже коммерческая; v8.5.1 (декабрь 2025) — последняя Apache-2.0, с патчами,
   заявленными минимум до конца 2026.
2. **Шина была внутрипроцессной.** Вынос модуля (`FamilyHub.Modules.Medical`,
   `FamilyHub.Modules.Birthdays`) в отдельный сервис требовал бы переписывания шины, а не
   переконфигурации — не было топологии, которую можно перенаправить на брокер без замены кода.

## Решение

1. **MassTransit 8.5.1, не v9.x.** Та же лицензионная логика, что уже применена к MediatR
   12.5.0 в этом решении. Транспорт для внутренней доставки — **InMemory**
   (`UsingInMemory`) — 7 текущих потребителей остаются в процессе монолита, но контракты и
   потребители не завязаны на транспорт: смена на `UsingRabbitMq`/Kafka-как-основную-шину при
   реальном выносе модуля в отдельный сервис — правка одной строки конфигурации в
   `MassTransitRegistration.cs`, не переписывание потребителей.
2. **EF Core Transactional Outbox (`AddEntityFrameworkOutbox<AppDbContext>` + `UseBusOutbox()`)
   заменяет собственный Postgres-outbox.** Публикация и бизнес-запись остаются в одной
   транзакции (то же свойство, что давал `IOutboxWriter.Enqueue`), но полинг/диспетчеризацию,
   ретраи и dedup-окно (`Messaging:Outbox:{QueryDelay,QueryMessageLimit,DuplicateDetectionWindow}`)
   теперь делает библиотека, а не `OutboxDispatcher`/`EventTypeRegistry`
   (оба удалены, `src/FamilyHub.Infrastructure/Outbox/` целиком).
3. **Контракты событий (`FamilyHub.Contracts/Events/*.cs`) не зависят от шины.** `IDomainEvent`/
   `DomainEvent` удалены — `EventId`/`OccurredAt` заменены конвертом MassTransit
   (`ConsumeContext.MessageId`/`SentTime`); `record`-события — обычные POCO, `Contracts.csproj`
   стал сборкой без единого `PackageReference`. Маршрутизация — по message URN
   (`urn:message:FamilyHub.Contracts.Events:...`), встроенному в конверт, без ручной регистрации
   типов.
4. **`IDomainEventPublisher` — тонкая обёртка над `IPublishEndpoint`, а не голая инъекция шины.**
   Единственный узкий проход публикации не даёт случайно инжектировать `IBus` — тот минует EF
   Outbox и публикует немедленно, тихо теряя транзакционность гарантии «событие уходит только
   если закоммитилась бизнес-запись».
5. **7 `INotificationHandler<T>` → `IConsumer<T>`.** Механическая конвертация,
   `EventHandlers/` → `Consumers/`. Поведенческое отличие от `IsolatingLoggingPublisher`:
   MassTransit даёт один receive endpoint на потребителя — падение одного не касается соседа
   вообще (топология, а не наш код). Проверено при переносе: ни один из 7 потребителей не
   полагался на совместный ретрай всей строки; `UserLeftFamilyEvent` — единственное событие с
   двумя потребителями (Notifications + Medical), оба независимо идемпотентны
   (`ExecuteDeleteAsync`, `DedupKey`+UNIQUE).
6. **Kafka — только как внешний фан-аут через bridge-consumer, не прямая интеграция с outbox.**
   `UseBusOutbox()` перехватывает scoped `IPublishEndpoint` основной шины, но не
   `ITopicProducer<T>` Rider'а (отдельный `IBusInstance` со своей абстракцией продюсера) —
   публикация через Rider не получает транзакционность outbox напрямую. Решение:
   `KafkaTopicBridgeConsumer<T> : IConsumer<T>` — обычный at-least-once потребитель ОСНОВНОЙ
   шины, чья единственная задача — `producer.Produce(context.Message)`. Гарантия долговечности
   остаётся там, где уже решена (бизнес-запись + outbox-строка атомарны; outbox → InMemory-шина
   устойчива до доставки) — пересылка в Kafka лишь ещё один at-least-once потребитель того же
   события, ничем не отличающийся от Notifications/Medical-потребителей.
7. **Явные Kafka-топик-константы (`KafkaTopics.cs`), не рефлексия из имени типа.** Переименование
   C#-класса события не должно тихо переименовывать боевой топик. Покрытие всех
   `FamilyHub.Contracts.Events` топиками проверяет `KafkaTopicsTests`
   (`DomainEventTypes.All` ⊆ `KafkaTopics.ByEventType.Keys`).
8. **152-ФЗ: явный `retention.ms`/`KAFKA_LOG_RETENTION_HOURS=168`** (тот же срок, что был у
   `OutboxOptions.ProcessedRetention`) — часть событий несёт ПДн/медицинские данные
   (`BirthdayApproachingEvent.PersonName`, `MedicationExpiringEvent.Name`,
   `MedicationEnrichedEvent.DisplayName`). Брокер — только self-hosted внутри контура РФ
   (docker-compose, никогда управляемый облачный Kafka вне юрисдикции — ADR-0001 без изменений).
   Без ключа партиционирования (round-robin) — добавление `IFamilyScopedEvent` в Contracts сейчас
   было бы преждевременной сложностью сразу после того, как убрали `IDomainEvent` оттуда же; нет
   реального внешнего потребителя, которому важен порядок в разрезе семьи.
9. **`apache/kafka` (ASF, KRaft, Apache-2.0) в `docker-compose.yml`; `confluentinc/cp-kafka`
   (Confluent Community License) только в тестах** (`Testcontainers.Kafka` 3.10.0, не 4.x —
   совместимость с уже используемыми `Testcontainers.PostgreSql`/`Testcontainers.Minio` 3.10.0).
   CCL ограничивает продажу как SaaS, не локальное использование в CI/тестах — та же логика
   лицензионного разделения, что и у выбора MassTransit 8.5.1 (не 9.x), применённая к образу
   тестового брокера отдельно от продового.
10. **`Messaging:Kafka:Enabled` — конфиг-переключатель**, тот же идиом, что `Enrichment:Provider`.
    `false` (дефолт в `appsettings.json`, во всех тестовых хостах кроме `KafkaWebFactory`) даёт
    чистый InMemory-режим без единого обращения к брокеру; `docker-compose.yml` включает Kafka
    явно через `Messaging__Kafka__Enabled=true`.

## Последствия

- **Ретраи больше не переживают рестарт процесса.** `UseMessageRetry` (per-consumer,
  `Messaging:Retry:*`) держит попытки в памяти, а не в Postgres, в отличие от старого
  `Attempts`/`NextAttemptAt` на строке outbox. Смягчение: все потребители идемпотентны,
  `ReminderScanJob` перепубликует ежедневно. Настоящее решение той же проблемы — долговечный
  транспорт (RabbitMQ/Kafka как основная шина), а не полумера в этом ADR.
- **Нет колонки `Error` для grep по зависшим событиям.** Диагностика полностью уходит в
  Serilog/Seq. `InboxState`/`OutboxState`/`OutboxMessage` заведены миграцией
  (`ReplaceOutboxWithMassTransit`), но фильтр дедупликации на уровне шины (`InboxState`) не
  включён — у каждого потребителя уже есть дедуп на уровне бизнес-логики (`DedupKey`+UNIQUE,
  `ExecuteDeleteAsync`); включение добавило бы лишнюю запись и блокировку без выгоды.
- **`/dev/trigger-outbox-dispatch` удалён без замены.** У MassTransit нет поддерживаемого API
  «прогнать доставку сейчас» — `UseBusOutbox` будит delivery service сразу после `SaveChanges`,
  иначе полинг по `Messaging:Outbox:QueryDelay` (в тестовых хостах ускорен до 200мс). Тесты и
  локальная отладка перешли на полинг-хелпер `WaitForAsync` вместо форс-диспатча;
  `/dev/trigger-reminder-scan` (синхронный вызов `ReminderScanJob.RunAsync` напрямую, не через
  шину) сохранён без изменений.
- **Kafka Rider архитектурно отделён от основной шины** — нет request/response, саг и общего
  outbox с InMemory-транспортом. Это осознанный компромисс: Rider здесь — витрина наружу
  (topic-oriented fan-out), а не механизм внутренней доставки.
- Переход между InMemory и реальным брокером как основной шиной (при выносе модуля в
  микросервис) не требует пересмотра контрактов, потребителей или `IDomainEventPublisher` —
  только `MassTransitRegistration.cs`.

## Связанные решения

- [ADR-0001](0001-data-locality-and-egress.md) — РФ-контур; self-hosted Kafka внутри него, egress-политика не расширяется.
- [ADR-0005](0005-medication-enrichment-egress.md) — прежний прецедент лицензионно-обусловленного выбора провайдера/образа под РФ-контур.
- `.claude/patterns/backend.md` — чек-лист «новое доменное событие» (контракт → `IDomainEventPublisher` → `IConsumer<T>` → `KafkaTopics` → ПДн-ретеншн).
