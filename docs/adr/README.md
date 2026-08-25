# Architecture Decision Records

Журнал архитектурных решений FamilyHub. Формат: одно решение — один файл `NNNN-slug.md`
(статус, контекст, решение, последствия). Решения не переписываются задним числом:
пересмотр — новый ADR со ссылкой на отменяемый.

| # | Решение |
|---|---------|
| [0001](0001-data-locality-and-egress.md) | Локализация данных в РФ и контур исходящего трафика |
| [0002](0002-field-and-file-encryption-key-management.md) | At-rest шифрование и управление ключами |
| [0003](0003-search-architecture.md) | Архитектура поиска: Postgres FTS + in-memory, отказ от OpenSearch |
| [0004](0004-web-push-egress-exception.md) | Web Push: исключение из egress-политики ADR-0001 |
| [0005](0005-medication-enrichment-egress.md) | Обогащение справочника препаратов: исключение из egress-политики ADR-0001 |
| [0006](0006-event-bus-masstransit-and-kafka.md) | Событийная шина: MediatR → MassTransit 8.5.1 + Kafka Rider (частично пересмотрен ADR-0007) |
| [0007](0007-kafka-as-primary-event-transport.md) | Kafka Rider — реальный транспорт для бизнес-потребителей событий, не только внешний мост |
| [0009](0009-admin-panel-and-key-rotation.md) | Админ-панель (статистика + ротация ключей) и связки ключей Encryption/Jwt/Attachments |
