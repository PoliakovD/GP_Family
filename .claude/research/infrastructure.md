# `FamilyHub.Infrastructure`

Сквозные технические сервисы, на которые опираются `FamilyHub.Api` и все `*.Modules.*`.
Ничего здесь не знает о конкретных бизнес-сущностях модулей (Medication/Birthday/...),
только о `Domain`.

## Аутентификация (`Auth/`)

Два независимых `AuthenticationHandler`, переключаемых policy-схемой `"Smart"` в `Program.cs`
(по наличию заголовка `X-Dev-TelegramId`):

- **`TelegramMiniAppAuthenticationHandler`** (схема `AuthSchemes.TelegramMiniApp`, прод) —
  принимает `Authorization: tma <initData>` (или `X-Telegram-Init-Data` как запасной
  вариант для отладки), валидирует через `ITelegramInitDataValidator`, провижинит
  пользователя через `IUserProvisioningService`, кладёт `FamilyHubClaimTypes.UserId`/`TelegramId`
  в claims.
- **`DevAuthenticationHandler`** (схема `AuthSchemes.Dev`, **только Development**) —
  заголовок `X-Dev-TelegramId: <id>`, тот же провижининг, без HMAC-проверки. Регистрируется
  условно в `Program.cs` (`if (isDevelopment)`) — структурно невозможно случайно включить в проде.

`TelegramInitDataValidator` (`Telegram/`) — точная реализация официального алгоритма Telegram
(`secret_key = HMAC_SHA256("WebAppData", botToken)`, дальше HMAC от
data-check-string, сравнение `CryptographicOperations.FixedTimeEquals`). Дополнительно проверяет
`auth_date` на свежесть (`TelegramOptions.MaxInitDataAge`). Без `Telegram:BotToken` валидатор
всегда возвращает `null` (отказывает, не пропускает) — это намеренно, не баг.

## Авторизация по ролям (`Authorization/`)

Два параллельных способа применить одну и ту же проверку (выбирайте по контексту):

- **`IFamilyAccessService.HasRoleAsync(userId, familyId, minRole)`** — императивный вызов
  внутри сервиса. Используется во всех CRUD-сервисах модулей (`MedicationService`,
  `BirthdayService`, ...) перед каждой операцией.
- **`FamilyRoleHandler : AuthorizationHandler<FamilyRoleRequirement, IFamilyOwned>`** —
  resource-based декларативная авторизация ASP.NET Core поверх того же запроса
  (`membership.Status == Active && membership.Role >= requirement.MinRole`). На практике в
  эндпоинтах модулей сейчас используется первый способ (явный вызов сервиса), а не
  `IAuthorizationService.AuthorizeAsync` — хендлер зарегистрирован в DI и доступен, если
  понадобится декларативный вариант для нового модуля.

`options.FallbackPolicy = options.DefaultPolicy` в `Program.cs` означает: **любой эндпоинт без
явной политики/`.AllowAnonymous()` требует аутентификации по умолчанию**. Это касается и
нераспознанных путей — `MapFallbackToFile("index.html")` отдаёт SPA только для GET/HEAD именно
потому, что несёт `.AllowAnonymous()`; любой непойманный non-GET запрос всё равно получит 401
от `FallbackPolicy`, а не 404 (проверено эмпирически, это не баг).

`ClaimsPrincipalExtensions.GetUserId()` — единая точка чтения `FamilyHubClaimTypes.UserId` из
`ClaimsPrincipal` (используется и в `FamilyRoleHandler`, и в `ICurrentUser`).

## Текущий пользователь и провижининг (`CurrentUser/`)

- **`ICurrentUser`** (`HttpContextCurrentUser`) — обёртка над `HttpContext.User`, даёт `UserId`
  эндпоинтам без прямой работы с `ClaimsPrincipal`.
- **`IUserProvisioningService.GetOrCreateUserIdAsync(telegramId, displayName)`** — find-or-create
  по `TelegramId` с защитой от гонки: если `INSERT` упал на UNIQUE-индексе (два параллельных
  первых запроса одного нового Telegram-пользователя), перечитывает уже вставленную строку
  вместо падения. Вызывается обоими auth-хендлерами и `TelegramUpdateHandler` (бот) — единая
  точка создания пользователей независимо от точки входа.

## Персистентность (`Persistence/`)

- `AppDbContext` + `Configurations/*Configuration.cs` (один файл на сущность, `IEntityTypeConfiguration<T>`) —
  здесь и только здесь должны жить UNIQUE-индексы/связи, а не как проверки "на честность" в сервисах.
- Миграции (`Migrations/`): `InitialCreate`, `AddNotifications`. Подключение — `ConnectionStrings:Postgres`.
- `DesignTimeDbContextFactory` — для `dotnet ef`. **Известная проблема**: design-time tooling
  не всегда подхватывает `ASPNETCORE_ENVIRONMENT` для `appsettings.Development.json` — см.
  `TECH_DEBT.md` п.2 (воркэраунд: `--connection` флагом).

## Файловое хранилище (`Storage/`)

`IFileStorage` (`SaveAsync`/`OpenReadAsync`/`DeleteAsync`) — единственная реализация
**`MinioFileStorage`**, в т.ч. в Development (`LocalFileStorage` упразднён: раньше запуск API из
IDE без Docker тихо писал сканы на диск мимо объектного хранилища, и этот путь не проверялся).
`Program.cs` fail-fast'ит на старте, если `Minio:Endpoint`/`AccessKey`/`SecretKey` не заданы —
в стиле уже существующих проверок `Encryption:MasterKey`/`Attachments:DownloadSigningKey`.

Presigned-ссылки самого MinIO **не используются** (упразднены ADR-0002 — они отдавали бы
шифротекст, блоб зашифрован целиком `IFileCipher` до записи). Доступ к файлу — только через
собственный API-эндпоинт с расшифровкой, по короткоживущей HMAC-подписанной ссылке
(`DownloadTokenService`, TTL `Attachments:UrlTtl` = 5 минут по умолчанию):
`GET /api/attachments/{attachmentId}/file?expires=&sig=`, `.AllowAnonymous()` — подлинность
проверяется подписью, не аутентификацией.

Ключ объекта в бакете — `StorageKeyFactory.Create(attachmentId)`, полностью непрозрачный
(`blobs/{a}/{b}/{attachmentId}`, `{a}{b}` — первые байты `attachmentId` как двухуровневый шард):
ни типа записи, ни владельца, ни группировки нескольких сканов по одной медкарте в ключе нет —
администратор хранилища и его служебные логи видят только набор несвязанных шифроблобов, связь
blob ↔ запись живёт единственно в `medical."FileAttachments"."StorageKey"`.

## Оповещения (`Notifications/`)

- **`ReminderScanJob`** — ежедневная Hangfire recurring job (регистрируется через DI
  `IRecurringJobManager` в `Program.cs`, **не** через статический `RecurringJob.AddOrUpdate` —
  см. `TECH_DEBT.md`/историю фикса). Сканирует сроки годности лекарств и приближающиеся дни
  рождения, создаёт `Notification` идемпотентно (UNIQUE `DedupKey`, гонка ловится
  `catch (DbUpdateException)`), затем рассылает все ещё не отправленные (`SentAt == null`,
  включая зависшие с прошлых прогонов) через `INotificationSender`.
- **`INotificationSender`** — абстракция доставки. Две реализации, переключаемые в `Program.cs`
  по наличию `Telegram:BotToken`:
  - `LoggingNotificationSender` — просто пишет в лог (dev без бота).
  - `TelegramNotificationSender` — резолвит `Notification.UserId → User.TelegramId`, шлёт
    `bot.SendMessage` с кнопкой «Открыть FamilyHub» (`WebAppInfo(MiniAppUrl)`). Любая ошибка
    отправки (включая отсутствие `TelegramId`) — логируется и **проглатывается**, не
    пробрасывается: `ReminderScanJob.SendPendingAsync` идёт по списку одним циклом и одним
    `SaveChangesAsync` после — необработанное исключение оборвало бы весь батч.

## Telegram-интеграция (`Telegram/`)

- `TelegramOptions` — `BotToken`, `WebhookSecret`, `WebhookUrl`, `MiniAppUrl`, `MaxInitDataAge`.
  Реальный `BotToken` — через user-secrets/переменные окружения, никогда в `appsettings*.json`
  в репозитории.
- `ITelegramInitDataValidator`/`TelegramInitDataValidator` — см. «Аутентификация» выше.
- Сам клиент бота (`ITelegramBotClient`) регистрируется в `Program.cs` (не здесь) — см.
  `api-core.md` → `Program.cs`.
