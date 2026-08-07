# Технический долг

Зафиксированные ограничения и нерешённые мелочи, обнаруженные по ходу разработки.
Не блокируют текущий v1, но стоит учитывать при дальнейшей работе.

## 1. `dotnet ef` не подхватывает `ASPNETCORE_ENVIRONMENT` для design-time конфигурации

`dotnet ef database update`/`dotnet ef dbcontext info`, запущенные из `src/FamilyHub.Api`,
всегда резолвили строку подключения на БД `familyhub` (из `appsettings.json`), даже когда
`ASPNETCORE_ENVIRONMENT=Development` был явно выставлен и через Bash `export`, и через
PowerShell `$env:`. Ожидалось переключение на `familyhub_dev` (`appsettings.Development.json`).

Причина не диагностирована до конца — похоже на особенность того, как design-time хост
`dotnet-ef` в этом проекте поднимает `WebApplication`/конфигурацию.

**Воркэраунд:** передавать явный `--connection "Host=localhost;Port=5432;Database=familyhub_dev;Username=postgres;Password=postgres"`
флагом в `dotnet ef`-команды, когда нужно работать с dev-базой.

## 2. `docker-compose.yml` создаёт не ту БД, на которую указывает dev-конфиг

`POSTGRES_DB: familyhub` в `docker-compose.yml` создаёт только базу `familyhub`, а
`appsettings.Development.json` указывает на `familyhub_dev`. После `docker compose up -d postgres`
с нуля база `familyhub_dev` не существует, и API падает с
`Npgsql.PostgresException: database "familyhub_dev" does not exist`, пока её не создать
вручную (см. п.1 — миграции с явным `--connection` создают её как побочный эффект).

**Чтобы закрыть:** либо переименовать `POSTGRES_DB` в `familyhub_dev` в compose, либо завести
в compose обе базы, либо привести имя в `appsettings.Development.json` к `familyhub`.

## 3. Публичная регистрация вебхука бота не настроена (вне объёма v1)

`TelegramWebhookRegistrar` вызывает `SetWebhook`/`SetChatMenuButton` только если заданы
`Telegram:BotToken` и `Telegram:WebhookUrl` — для локальной разработки это осознанно
пропускается. Реальный паблик-домен с HTTPS и проверка `setWebhook` против настоящего
Telegram-сервера не проверялись (тестировался только маршрут `/bot/webhook` синтетическим
запросом). Нужно сделать при деплое.

Один конкретный дефект из этого класса уже найден и исправлен по факту реального
использования: `allowedUpdates` в `SetWebhook` не включал `UpdateType.CallbackQuery`, из-за
чего Telegram на своей стороне никогда не доставлял апдейты нажатий инлайн-кнопок (в т.ч.
"Привязать"/"Отмена" в `TelegramUpdateHandler.HandleCallbackQueryAsync`) — `/bot/webhook`
их вообще не получал. Прикладная логика была написана верно с самого начала и покрыта
тестами, но тесты синтезируют `Update` и шлют его напрямую, минуя `setWebhook`, поэтому
дефект не ловился (см. регрессионный тест `TelegramWebhookRegistrarTests`). Остальное по
этому пункту (широкая проверка деплоя с реальным паблик-доменом) по-прежнему не сделано.

## 4. Монетизация и чат/календарь вне объёма v1

Монетизация/лимиты (этап 5 брифа) и чат/календарь (этап 6+) — намеренно вне объёма текущей
работы. (Шифрование вложений — реализовано, `AttachmentService`/`IFileCipher`; эта строка была
устаревшей и удалена.)
