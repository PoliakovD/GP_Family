# Технический долг

Зафиксированные ограничения и нерешённые мелочи, обнаруженные по ходу разработки.
Не блокируют текущий v1, но стоит учитывать при дальнейшей работе.

## 1. Нет эндпоинта для получения списка вложений анализа

`GET /api/medical-records` не возвращает список уже загруженных вложений, и отдельного
`GET /api/medical-records/{id}/attachments` не существует — только `POST .../attachments`
(загрузка) и `GET /api/attachments/{id}/url` (presigned-ссылка по уже известному `attachmentId`).

Из-за этого Mini App (`MedicalRecordsTab.tsx`) показывает вложения только за текущую сессию
(хранит их в React state сразу после загрузки). Вложения, загруженные ранее или из бота,
не отображаются после перезагрузки страницы.

**Чтобы закрыть:** добавить `GET /api/medical-records/{id}/attachments` в
`FamilyHub.Modules.Medical`, прокинуть в `api.ts` и подгружать список в `MedicalRecordsTab`
при выборе записи.

## 2. `dotnet ef` не подхватывает `ASPNETCORE_ENVIRONMENT` для design-time конфигурации

`dotnet ef database update`/`dotnet ef dbcontext info`, запущенные из `src/FamilyHub.Api`,
всегда резолвили строку подключения на БД `familyhub` (из `appsettings.json`), даже когда
`ASPNETCORE_ENVIRONMENT=Development` был явно выставлен и через Bash `export`, и через
PowerShell `$env:`. Ожидалось переключение на `familyhub_dev` (`appsettings.Development.json`).

Причина не диагностирована до конца — похоже на особенность того, как design-time хост
`dotnet-ef` в этом проекте поднимает `WebApplication`/конфигурацию.

**Воркэраунд:** передавать явный `--connection "Host=localhost;Port=5432;Database=familyhub_dev;Username=postgres;Password=postgres"`
флагом в `dotnet ef`-команды, когда нужно работать с dev-базой.

## 3. `docker-compose.yml` создаёт не ту БД, на которую указывает dev-конфиг

`POSTGRES_DB: familyhub` в `docker-compose.yml` создаёт только базу `familyhub`, а
`appsettings.Development.json` указывает на `familyhub_dev`. После `docker compose up -d postgres`
с нуля база `familyhub_dev` не существует, и API падает с
`Npgsql.PostgresException: database "familyhub_dev" does not exist`, пока её не создать
вручную (см. п.2 — миграции с явным `--connection` создают её как побочный эффект).

**Чтобы закрыть:** либо переименовать `POSTGRES_DB` в `familyhub_dev` в compose, либо завести
в compose обе базы, либо привести имя в `appsettings.Development.json` к `familyhub`.

## 4. Публичная регистрация вебхука бота не настроена (вне объёма v1)

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

## 5. Шифрование сканов и лимиты этапа 5 не реализованы

`MedicalRecord.IsEncrypted` существует в модели, но фактическое шифрование вложений не
реализовано. Монетизация/лимиты (этап 5 брифа) и чат/календарь (этап 6+) — намеренно вне
объёма текущей работы.
