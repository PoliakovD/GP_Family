# Architecture Decision Records

Журнал архитектурных решений FamilyHub. Формат: одно решение — один файл `NNNN-slug.md`
(статус, контекст, решение, последствия). Решения не переписываются задним числом:
пересмотр — новый ADR со ссылкой на отменяемый.

| # | Решение |
|---|---------|
| [0001](0001-data-locality-and-egress.md) | Локализация данных в РФ и контур исходящего трафика |
| [0002](0002-field-and-file-encryption-key-management.md) | At-rest шифрование и управление ключами |
| [0003](0003-search-architecture.md) | Архитектура поиска: Postgres FTS + in-memory, отказ от OpenSearch |
