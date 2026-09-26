# ADR-0008. Telegram-бот — отдельный процесс, исходящий трафик через AmneziaWG-сайдкар

**Статус:** принят (реализован 2026-08-19, коммит `aade7e7` «бот сепарирован от api монолита для
рутинга трафика через awg»). **Восстановлен задним числом:** исходный файл ADR не сохранился,
хотя на него ссылаются `deploy/docker-compose.prod.yml`, `.github/workflows/build.yml`,
`.github/workflows/deploy.yml` и код. Текст собран по этим источникам и по коду
(`src/FamilyHub.TelegramBot`, `src/FamilyHub.Api/Features/Bot`, `deploy/`); мотивы, которых в
источниках нет, сюда не добавлены. Дополняет [ADR-0001](0001-data-locality-and-egress.md)
(egress к `api.telegram.org`) и [ADR-0007](0007-kafka-as-primary-event-transport.md) (Kafka).

## Контекст

Бот и Mini App — функциональная основа продукта, поэтому `api.telegram.org` — единственный
разрешённый ADR-0001 внешний адрес для Telegram. Но исходящий трафик к нему из прод-контура должен
идти **через туннель** (AmneziaWG), а не обычным egress хоста, и при этом остальной API (Postgres,
MinIO, LM Studio, почта, Web Push, поиск) ходить через этот туннель не должен. Пока обработка
апдейтов бота жила внутри `FamilyHub.Api` (монолит), развести эти потоки было невозможно:
процесс один — сетевой namespace один.

## Решение

### 1. Отдельный процесс `FamilyHub.TelegramBot`

Собственный csproj/образ (`gp_family-telegrambot`, `src/FamilyHub.TelegramBot/Dockerfile`), тот же
подход к сборке и логам, что у API (Serilog → Seq, `ShutdownTimeout` 30 с из-за Kafka Rider).
Бот отвечает за: вебхук (`/bot/webhook`, `BotEndpoints`), обработку апдейтов
(`TelegramUpdateHandler`), регистрацию вебхука (`TelegramWebhookRegistrar`), исходящие сообщения
пользователям.

**Никакого доступа к БД.** У бота нет ни строки подключения, ни `Encryption:MasterKey`; сервис в
compose намеренно **без `env_file: .env`** — ему не нужны чужие секреты.

### 2. Контракт «бот ↔ API»

- **Команды бота → API — внутренний HTTP** `/internal/bot/*` (`IFamilyHubApiClient` в боте,
  `InternalBotEndpoints` в API): `ping`, `users/resolve`, `invites/redeem`,
  `telegram-link/peek`, `telegram-link/confirm`.
- **Авторизация** — заголовок `X-Internal-Token` (`InternalBotAuthFilter`): сравнение за
  постоянное время; **не сконфигурированный секрет — отказ, а не пропуск**. Это вторая линия защиты:
  `/internal/*` дополнительно заблокирован в `deploy/Caddyfile`, порт `api:8080` наружу не
  публикуется.
- **Оповещения API → бот — Kafka-топик `telegram-outbound`**: `TelegramOutboundPublisher` (API,
  `NotificationChannelsRegistration`) → `TelegramOutboundConsumer` (бот, consumer group
  `bot-telegram-outbound`). Единственный топик, который API публикует, но не потребляет.
  Бот держит только consumer-часть шины (без EF Outbox).

### 3. Сетевая изоляция: сайдкар `wg-client`

- Образ `gp_family-wg-client` собирается **из исходников** (`deploy/wg/Dockerfile`:
  `amneziawg-go` + `amneziawg-tools`, коммиты закрепляются `AMNEZIAWG_GO_COMMIT`/
  `AWGTOOLS_COMMIT`) и всегда собирается в `build.yml`, чтобы образ был готов к включению профиля.
- `telegrambot` работает с `network_mode: "service:wg-client"` — **делит с ним сетевой namespace**.
  Поэтому его исходящий трафик к `api.telegram.org` идёт через туннель, а обычный egress API не
  тронут. Побочные следствия: у бота нет своего имени в compose-сети (Caddy обращается к нему как к
  `wg-client:8080`), а `ports`/`networks`/`hostname`/`dns` для него запрещены Docker.
- Конфиг туннеля — секрет `WG_CONF`: `deploy.yml` рендерит его в `wg/awg0.conf`
  (интерфейс `awg0`, монтируется в контейнер read-only). `entrypoint.sh` завершается с ошибкой,
  когда интерфейс пропал, — чтобы `restart: unless-stopped` пересоздал контейнер, а не оставил его
  «живым» без туннеля.
- `caddy` направляет `/bot/webhook` на `wg-client:8080`.

### 4. Необязательность и fail-soft

- Сервисы `wg-client` и `telegrambot` в **профиле `bot`** (`COMPOSE_PROFILES=bot`) — обычный
  деплой их не запускает. На `caddy` нет `depends_on` на `wg-client` (иначе `docker compose config`
  падает, пока профиль выключен), поэтому без бота `/bot/webhook` просто отвечает 502.
- Переменные бота в compose — `${VAR:-}`, не required (Compose интерполирует весь файл до
  фильтра профилей). Отсутствие токена/`WebhookUrl` — fail-soft на уровне приложения: хост
  поднимается, но вебхук не регистрирует.
- Health: `/health/ready` — Kafka; `/health/telegram` — отдельно доказывает egress именно через
  туннель и **не** входит в готовность (недоступность Telegram не должна валить контур);
  `/health/telegram` доступен только через WireGuard (`bot.{домен}:8443`).

## Последствия

- Для нового кода: всё, что нужно боту от данных, добавляется как эндпоинт `/internal/bot/*` +
  метод `IFamilyHubApiClient`, а не как прямой доступ к БД; новые исходящие сообщения — событием
  в `telegram-outbound`.
- Бот — ещё один Kafka-клиент: при рестарте действует то же окно вступления в consumer group, что
  у API, поэтому его `/health/ready` тоже не требует шину первые 2 минуты, а `start_period` в
  compose — 90 с (см. `.claude/patterns/backend.md`, «Health-проба с окном прогрева»).
- Туннель — новая точка отказа только для Telegram-канала; остальное приложение (PWA, e-mail, Web
  Push) от него не зависит.
- Сайдкар собирается из `master` upstream, пока не закреплены коммиты (`AMNEZIAWG_GO_COMMIT`,
  `AWGTOOLS_COMMIT`) — воспроизводимость только «на момент сборки». В `deploy/wg/Dockerfile`
  прямо отмечено: сборка не проверялась против конкретного Amnezia-сервера, проверять `getMe` через
  туннель нужно на реальном конфиге.
