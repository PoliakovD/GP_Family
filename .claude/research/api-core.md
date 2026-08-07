# `FamilyHub.Api`

Composition root приложения (`Program.cs`) + core-фичи, которые не выделены в отдельные
модули (семьи, инвайты, участники, оповещения, бот). Раздаёт также собранный Mini App
(`wwwroot`, см. `web-miniapp.md`).

## `Program.cs` — что и в каком порядке

1. Конфигурация: `TelegramOptions`, `MinioOptions`, `NotificationOptions`.
2. `AppDbContext` (Postgres), `ICurrentUser`/`IUserProvisioningService`, `ITelegramInitDataValidator`.
3. `IFileStorage` — единственная реализация MinioFileStorage, в т.ч. в Development
   (`LocalFileStorage` упразднён); fail-fast на старте без `Minio:Endpoint/AccessKey/SecretKey`
   (см. `infrastructure.md`).
4. Core-сервисы: `FamilyService`, `InviteService`, `MembershipService`.
5. Авторизация: `IFamilyAccessService`, `IAuthorizationHandler` (`FamilyRoleHandler`),
   `FallbackPolicy = DefaultPolicy` (всё требует аутентификации по умолчанию).
6. Аутентификация: схема `"Smart"` (policy-схема, выбирает Dev/TelegramMiniApp по заголовку)
   только в Development; в проде — прямо `TelegramMiniApp`.
7. Hangfire: `AddHangfire` + `AddHangfireServer`, `ReminderScanJob`, `NotificationService`.
8. **Бот — всё зависящее от `ITelegramBotClient` регистрируется только если
   `Telegram:BotToken` задан** (`telegramBotConfigured`): `ITelegramBotClient`,
   `INotificationSender → TelegramNotificationSender`, `TelegramUpdateHandler`,
   `TelegramWebhookRegistrar` (hosted service). Иначе — `INotificationSender → LoggingNotificationSender`,
   и весь бот-слой просто не существует в DI-графе. **Важно**: эти регистрации должны
   оставаться синхронно за одним и тем же флагом — рассинхронизация (что-то зависящее от
   `ITelegramBotClient` зарегистрировано безусловно) валится с `InvalidOperationException`
   при старте в Development (`ValidateOnBuild`), но молча "выстрелит" 500 при первом
   обращении в других окружениях.
9. `AddMedicalModule()`, `AddBirthdayModule()` — DI новых модулей.
10. После `app.Build()`: `UseDefaultFiles`/`UseStaticFiles` **до** `UseAuthentication`/`UseAuthorization`
    (статика Mini App отдаётся без аутентификации); затем маршруты модулей
    (`MapFamilyEndpoints`, `MapInviteEndpoints`, `MapMemberEndpoints`, `MapMedicalModule`,
    `MapBirthdayModule`, `MapNotificationEndpoints`, условно `MapBotEndpoints`); затем
    `MapFallbackToFile("index.html").AllowAnonymous()` (SPA fallback — обязателен
    `AllowAnonymous`, иначе `FallbackPolicy` зарубит до того, как дело дойдёт до React);
    `MapHangfireDashboard("/hangfire")` только в Development.
11. Регистрация recurring job — через `app.Services.GetRequiredService<IRecurringJobManager>()`,
    **не** статический `RecurringJob.AddOrUpdate` (см. `infrastructure.md`).

Любой новый модуль подключается так же: `builder.Services.AddXModule()` перед `Build()`,
`app.MapXModule()` после.

## Семьи, инвайты, участники (`Features/Families`, `Features/Invites`, `Features/Members`)

Эти три фичи тесно связаны (общий жизненный цикл членства), но разнесены по сервисам с чёткой
ответственностью:

- **`FamilyService`** — создание семьи (создатель сразу `Admin`+`Active`), список своих семей
  (`FamilySummary(Id, Name, MyRole, MyStatus)`, включая `PendingApproval`-заявки — пользователь
  видит сам факт, что подал заявку).
- **`InviteService`** — вся логика раздела 8 брифа в одном сервисе:
  - `CreateInviteAsync` — только `Admin`. Персональный инвайт (`TargetUserId` задан) всегда
    `MaxUses=1`; ссылка — `MaxUses` из запроса.
  - `RedeemInviteAsync` — проверки по порядку: `NotFound` → `Revoked` → `Expired` →
    `Exhausted` → `NotForYou` → `AlreadyMember` → создание `FamilyMember`+`FamilyInviteRedemption`+
    инкремент `UsedCount` **в одной транзакции** (защита от гонки на `MaxUses` при параллельных
    редимах одной многоразовой ссылки). Гибридное правило: персональный → `Active`
    (`RedeemResult.Joined`), ссылка → `PendingApproval` (`RedeemResult.PendingApproval`).
  - `RevokeInviteAsync`, `GetPendingMembersAsync`/`ApproveMemberAsync`/`RejectMemberAsync` —
    все требуют `Admin`. Отказ (`RejectMemberAsync`) удаляет membership, но **не**
    декрементирует `UsedCount` инвайта (сознательное упрощение, см. комментарий в коде).
- **`MembershipService`** — выгон (`RemoveMemberAsync`, только `Admin`) и самовыход
  (`LeaveFamilyAsync`, без требования роли) делят общую core-логику
  (`RemoveMembershipCoreAsync`): нельзя убрать последнего активного `Admin`
  (`LastAdmin`-результат), и при выходе/выгоне автоматически чистится `FamilyMedicalShare`
  ушедшего для этой семьи (его анализы перестают быть видны — сами записи и сканы остаются у
  владельца).

## Оповещения (`Features/Notifications/NotificationEndpoints.cs`, `NotificationService.cs`)

Только REST для просмотра/чтения — создание и доставка целиком в `ReminderScanJob`/
`INotificationSender` (Infrastructure). `GetMyNotificationsAsync` — строго по `UserId`
получателя (не по роли в семье), опциональный фильтр `unreadOnly`.

## Бот как webhook (`Features/Bot/`)

Тонкий клиент: вся бизнес-логика делегируется в существующие сервисы (`IUserProvisioningService`,
`InviteService`, `FamilyService`), здесь только маппинг Telegram-команд.

- **`BotEndpoints.MapBotEndpoints()`** — `POST /bot/webhook`, `.AllowAnonymous()` (аутентификация
  Telegram-style через заголовок, не через ASP.NET auth-схему):
  1. Сверяет `X-Telegram-Bot-Api-Secret-Token` с `TelegramOptions.WebhookSecret`
     (`CryptographicOperations.FixedTimeEquals`) — **первый шаг**, до парсинга тела. Не совпал → `401`.
  2. Десериализует тело в `Telegram.Bot.Types.Update` сериализатором библиотеки
     (`Telegram.Bot.JsonBotAPI.Options`).
  3. Передаёт в `TelegramUpdateHandler.HandleAsync`, возвращает `200 OK`.
- **`TelegramUpdateHandler`** — команды:
  - `/start` без аргумента → приветствие + inline-кнопка `WithWebApp("Открыть FamilyHub", MiniAppUrl)`.
  - `/start <inviteCode>` (deep-link) → провижин пользователя по `message.from.id`/`first_name`
    через `IUserProvisioningService`, затем `InviteService.RedeemInviteAsync`, ответ на
    `RedeemResult` человекочитаемым русским текстом.
  - `/help` и нераспознанное → краткая справка + та же кнопка.
  - Реальная отправка ответа идёт через `ITelegramBotClient.SendMessage` — это настоящий
    сетевой вызов к Telegram API; с фейковым/дев-токеном завершится ошибкой **после** того, как
    бизнес-логика (провижининг/редимит) уже выполнилась и закоммитилась (проверено синтетическим
    webhook-запросом в этой сессии).
- **`TelegramWebhookRegistrar`** (`IHostedService`) — при старте, если заданы `BotToken` **и**
  `WebhookUrl`, вызывает `SetWebhook`+`SetChatMenuButton`. Без публичного `WebhookUrl` (локальный
  dev) тихо пропускает — это не ошибка, а ожидаемое поведение для среды без HTTPS-домена.

Маршрут существует в роутинге, только когда `telegramBotConfigured == true` (см. `Program.cs`
выше) — без токена `/bot/webhook` не маппится вообще (любой запрос туда без токена получит 401
от `FallbackPolicy`, не специфическую ошибку — см. `infrastructure.md`).

## Точная карта маршрутов (актуальна на момент написания)

| Группа | Маршруты |
|---|---|
| Families | `POST /api/families`, `GET /api/families` |
| Invites | `POST /api/families/{familyId}/invites`, `POST /api/invites/{code}/redeem`, `POST /api/invites/{inviteId}/revoke`, `GET /api/families/{familyId}/pending`, `POST /api/families/{familyId}/members/{targetUserId}/approve`, `POST /api/families/{familyId}/members/{targetUserId}/reject` |
| Members | `POST /api/families/{familyId}/members/{targetUserId}/remove`, `POST /api/families/{familyId}/leave` |
| Notifications | `GET /api/notifications?unreadOnly=`, `POST /api/notifications/{id}/read` |
| Bot | `POST /bot/webhook` (только если `Telegram:BotToken` задан) |
| Medications | `GET/POST /api/families/{familyId}/medications`, `PUT/DELETE /api/medications/{medicationId}` |
| Birthdays | `GET/POST /api/families/{familyId}/birthdays`, `PUT/DELETE /api/birthdays/{birthdayId}` |
| Medical records | `GET/POST /api/medical-records`, `POST /api/medical-records/share`, `POST /api/medical-records/unshare`, `POST /api/medical-records/{recordId}/hide`, `POST /api/medical-records/{recordId}/unhide` |
| Attachments | `POST /api/medical-records/{recordId}/attachments`, `GET /api/attachments/{attachmentId}/url` |

Все группы — `.RequireAuthorization()` по умолчанию (наследуется и от `FallbackPolicy`).
