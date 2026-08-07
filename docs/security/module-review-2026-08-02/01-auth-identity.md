# Модуль: Аутентификация и идентификация

**Файлы:** `Api/Features/Auth/*`, `Infrastructure/Auth/*`, `Infrastructure/Auth/Jwt/*`,
`Infrastructure/Telegram/*`, `Infrastructure/CurrentUser/*`, `Infrastructure/Authorization/*`,
`Infrastructure/Security/PasswordHasher.cs`, `TokenHasher.cs`, `TemporaryPasswordGenerator.cs`,
`Domain/ValueObjects/PasswordRules.cs`, `UsernameRules.cs`

**Статус:** 🔴 1/1 закрыта, 🟡 3/4 закрыты (находки 1, 3, 4, 5 — см. пометки ✅ у каждой; №2 —
трейд-офф коротких JWT, решение не принято, осталась открытой), 🟢 находка 6 закрыта (7/8 —
информационные, действий не требуют).

## Сводка

Два независимых способа входа (PWA email+пароль+JWT-сессия, Telegram Mini App с per-request
initData), плюс dev-заглушка. В целом модуль сделан аккуратно: HMAC-валидация initData,
constant-time сравнения, PBKDF2 210k итераций, анти-enumeration, lockout, ротация refresh-токенов
с reuse-detection. Основная находка — один эндпоинт, позволяющий необратимо заблокировать себе
доступ к аккаунту без серверной проверки.

## 🔴 Высокий приоритет

### 1. `POST /api/auth/telegram/revoke` не проверяет наличие пароля на сервере — риск необратимой самоблокировки

> ✅ **Исправлено.** `/revoke` теперь читает `Email`/`PasswordHash` и отказывает (409
> `{ code: "password_required" }`), если у пользователя нет другого подтверждённого способа
> входа — тем же паттерном, что и остальные guard'ы модуля. Покрыто интеграционным тестом
> (`TelegramBindingFlowTests.Revoke_TelegramOnlyAccountWithoutPassword_Returns409_...`).

- **Где:** `Api/Features/Auth/TelegramBindingEndpoints.cs:58-63`
- **Проблема:** эндпоинт просто обнуляет `TelegramId` для текущего пользователя:
  ```csharp
  group.MapPost("/revoke", async (ICurrentUser currentUser, AppDbContext db, CancellationToken ct) =>
  {
      await db.Users.Where(u => u.Id == currentUser.UserId)
          .ExecuteUpdateAsync(s => s.SetProperty(u => u.TelegramId, (long?)null), ct);
      return Results.Ok();
  });
  ```
  Нет проверки `PasswordHash != null`. Комментарий в `AuthService.revokeTelegram()` (фронтенд,
  `services/auth.service.ts:218-223`) прямо утверждает: «требует пароль для входа (иначе аккаунт
  остался бы вообще без способа войти)» — но это утверждение верно только для UI: кнопка
  «Отвязать» в `settings-security.component.html:83` показывается лишь при
  `me.hasTelegram && me.hasPassword`. Сам API-вызов это не проверяет.
- **Почему это важно:** пользователь, зарегистрированный ТОЛЬКО через Telegram (без email/пароля —
  штатный сценарий, `TelegramMiniAppAuthenticationHandler` + `UserProvisioningService`), находясь
  внутри Mini App (аутентифицирован per-request через initData), может вызвать этот эндпоинт
  напрямую (DevTools/curl/сторонний клиент) — `TelegramId` обнулится, а `PasswordHash` как был
  `null`, так и останется. После этого: `TelegramMiniAppAuthenticationHandler` — lookup-only, для
  обнулённого `TelegramId` больше не находит пользователя → 401 на всё; `PwaAuthService.LoginAsync`
  фильтрует `PasswordHash != null` → войти по email тоже нельзя, потому что email мог вообще не
  быть привязан. Аккаунт со всеми данными (мед-записи, аптечки, дни рождения) становится
  недостижим навсегда, без возможности восстановления через «Забыли пароль».
- **Рекомендация:** продублировать серверную проверку — отказывать (409/400), если
  `user.PasswordHash is null` (или в целом нет другого подтверждённого способа входа), тем же
  паттерном, что уже используется в `PwaAuthService`/`TelegramBindingService` для похожих guard'ов.

## 🟡 Средний приоритет

### 2. Отзыв сессии не инвалидирует уже выданный access-токен — до 15 минут окно

- **Где:** `Infrastructure/Auth/Jwt/TokenService.cs` (`RevokeAllForUserAsync`, `RevokeByIdAsync`),
  `JwtOptions.cs:20` (`AccessTokenLifetime = 15 минут`), UI-текст в
  `settings-security.component.ts:70-77` («Устройство потеряет доступ и будет разлогинено при
  следующем действии»).
- **Проблема:** JWT-валидация (`AddJwtBearer` в `Program.cs`) — чисто криптографическая (подпись +
  `exp`), без похода в БД на каждый запрос. Таблица `UserSessions` используется только для
  refresh-токенов. Значит «Завершить сессию» / logout-all / смена пароля отзывают только
  refresh-токен конкретного устройства — уже выданный, ещё не истёкший access-токен (до 15 минут)
  продолжает проходить аутентификацию на ЛЮБОМ эндпоинте, который не делает собственный запрос к
  `Users` (большинство — не делают, они просто доверяют claims).
- **Почему это важно:** это стандартный и осознанный trade-off коротких JWT (уже зафиксирован
  коротким TTL) — не «дыра», а недокументированный явно нюанс. Формулировка в UI
  («…будет разлогинено при следующем действии») слегка завышает мгновенность эффекта.
- **Рекомендация:** либо явно задокументировать 15-минутное окно как принятый риск (тогда просто
  закрыть пункт), либо (если для площадки критично мгновенное завершение — например, при жалобе на
  кражу устройства) добавить короткий allow/deny-лист отозванных `SessionId` в кэш, проверяемый в
  `OnTokenValidated`.

### 3. Timing-защита логина: путь «аккаунт не найден» тратит 2× PBKDF2 вместо 1×

> ✅ **Исправлено.** Dummy-хеш вынесен в `static readonly` поле (считается один раз при первом
> обращении к типу), путь для несуществующего email теперь зовёт только `Verify`.

- **Где:** `Api/Features/Auth/PwaAuthService.cs:118-128`
```csharp
if (user is null)
{
    PasswordHasher.Verify(password, PasswordHasher.Hash("Dummy0000"));
    return (LoginResult.InvalidCredentials, null, null);
}
```
- **Проблема:** `PasswordHasher.Hash(...)` сама по себе — это один прогон PBKDF2 (210 000
итераций) для генерации хеша, затем `Verify(...)` — ещё один прогон для сравнения. Итого 2
PBKDF2-операции на каждый логин с несуществующим email, тогда как реальный путь («аккаунт есть,
пароль неверный») — только 1 операция (`PasswordHasher.Verify(password, user.PasswordHash!)`).
Цель (не раскрывать таймингом факт существования аккаунта) достигается — путь для
несуществующего аккаунта не БЫСТРЕЕ, а даже медленнее — но ценой лишней CPU-нагрузки.
- **Почему это важно:** at scale это лёгкий множитель для DoS через флуд логинов с случайными
email (10 запросов/60 сек с IP по текущему rate-limit, но при большом ботнете/многих IP —
заметная лишняя нагрузка на CPU, вдвое больше, чем нужно).
- **Рекомендация:** заменить на фиксированный dummy-хеш, посчитанный один раз при старте процесса
(или захардкоженный валидный PBKDF2-хеш константой), и звать только `Verify` — тот же тайминг,
вдвое меньше работы.

### 4. Нет отдельного CSRF-механизма — весь расчёт на `SameSite=Lax`

> ✅ **Исправлено (взят более сильный вариант — добавлен полноценный токен, не только
> задокументирован риск).** Double-submit `IAntiforgery` поверх `SameSite=Lax`: публичная
> cookie `XSRF-TOKEN` (значение — `RequestToken`) + заголовок `X-XSRF-TOKEN` на каждый мутирующий
> `/api`-запрос PWA-сессии (глобальный гейт в `Program.cs`, Angular `withXsrfConfiguration`
> подставляет заголовок сама). Токен минтится только из аутентифицированного `GET /api/auth/me`
> (эмпирически проверено: `IAntiforgery` привязывает токен к identity запроса — минтинг на
> `AllowAnonymous` login/register/refresh не проходит валидацию на аутентифицированных
> эндпоинтах). `.DisableAntiforgery()` на upload-эндпоинтах теперь СНОВА осмыслен (не no-op) —
> см. обновлённую заметку в [03-medical-records-attachments.md]. См. также
> `docs/security/threat-model.md` (обновлена запись CSRF).

- **Где:** `Infrastructure/Auth/Jwt/PwaSessionCookieWriter.cs:20,27` (`SameSite = SameSiteMode.Lax`
для обоих cookie), `Program.cs` — нигде не вызывается `AddAntiforgery()`/`UseAntiforgery()`.
- **Проблема:** PWA-сессия целиком на httpOnly-cookie с `SameSite=Lax`. Это разумная базовая
защита от CSRF на большинстве современных браузеров, но это единственный слой — явного
анти-CSRF токена нет нигде. `AttachmentEndpoints.MapAttachmentEndpoints` зовёт
`.DisableAntiforgery()` на upload-эндпоинте — сейчас это no-op (отключать нечего), но выглядит
как копипаста из шаблона/ожидание, что антифорджери когда-то было/будет настроено.
- **Почему это важно:** `SameSite=Lax` не защищает от CSRF в сценариях с суб-доменами того же
registrable domain (если когда-то появится user-controlled контент на другом поддомене
`gp-family.ru`), и не защищает вовсе, если пользователь на старом браузере без поддержки
SameSite. Сейчас это, вероятно, приемлемый риск, но не задокументирован как осознанное решение.
- **Рекомендация:** явно зафиксировать в `docs/security/threat-model.md` (если ещё не зафиксировано),
что модель CSRF-защиты — исключительно `SameSite=Lax`, и это осознанный выбор; либо добавить
антифорджери-токен для мутирующих запросов.

### 5. Rate-limiting auth-эндпоинтов — только по IP

> ✅ **Зафиксировано как осознанно принятый риск** (ровно то, что рекомендовал сам аудит — не
> обязательно к действию). Добавлена явная запись в `docs/security/threat-model.md`
> ("Вне модели (осознанно)"): NAT/общий IP → возможные ложные 429, ротация IP тривиально обходит
> лимит; device-fingerprint/CAPTCHA не реализованы. Изменений в коде нет.

- **Где:** `Api/Features/Auth/AuthRateLimitOptions.cs`, `Program.cs:257-280`.
- **Проблема:** `AuthPermitLimit=10/60с`, `CodePermitLimit=3/час` — партиционируются по
`RemoteIpAddress`. Много пользователей за одним NAT/офисным прокси/мобильным оператором делят
один видимый IP → возможны ложные срабатывания (легитимные пользователи ловят 429). С другой
стороны, защита тривиально обходится ротацией IP (residential proxy).
- **Рекомендация:** не обязательно к действию прямо сейчас — просто зафиксировать как известное
ограничение текущей модели защиты от брутфорса (нет device-fingerprint/CAPTCHA слоя).

## 🟢 Низкий приоритет / на заметку

### 6. `GET /api/auth/me` падает 500 вместо чистого 401 при гонке с удалением аккаунта

> ✅ **Исправлено.** `SingleAsync` → `SingleOrDefaultAsync` + явный `401` при `null`. Покрыто
> регресс-тестом (`PwaAuthFlowTests.Me_WithStaleTokenAfterConcurrentAccountDeletion_...`),
> удаляющим строку `Users` напрямую через `DbContext` при ещё валидном access-токене.

- **Где:** `Api/Features/Auth/AuthEndpoints.cs:140`
  `var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == currentUser.UserId, ct);`
- Если пользователь был удалён параллельно (слияние аккаунтов, самостоятельное удаление с другого
  устройства) в узком окне, пока старый access-токен ещё валиден — `SingleAsync` бросает
  исключение → 500 вместо ожидаемого 401. Низкая вероятность, но стоит заменить на
  `SingleOrDefaultAsync` + явный 401 при `null`.

### 7. Код привязки Telegram (32 hex-символа) принимается ботом без троттлинга

- **Где:** `Api/Features/Bot/TelegramUpdateHandler.cs:74,83` (`LooksLikeLinkCode`).
- Любое сообщение боту, «похожее» на код (32 hex-символа), уходит в `HandleLinkStartAsync` без
  отдельного per-chat rate-limit — защита полностью держится на энтропии кода (128 бит,
  `RandomNumberGenerator.GetBytes(16)`), что более чем достаточно против брутфорса. Пункт чисто
  информационный — явного троттлинга на этом пути нет, но он и не нужен при такой энтропии.

### 8. `PasswordRules` не требует спецсимволов

- **Где:** `Domain/ValueObjects/PasswordRules.cs:18-24`.
- Осознанно, по докстрингу («без требований к спецсимволам — не запрашивалось»). Не находка, а
  фиксация того, что это сознательный выбор политики паролей, а не недосмотр — на случай, если
  requirements изменятся.

## ✅ Проверено, проблем не найдено

- HMAC-валидация Telegram initData (`TelegramInitDataValidator`) — корректный алгоритм по
  документации Telegram, constant-time сравнение хешей, проверка `auth_date` против
  `MaxInitDataAge`, отказ при отсутствии `Telegram:BotToken` (fail-closed, не fail-open).
- OTP-коды (`EmailOtpService`): хранятся только в виде SHA-256 хеша, constant-time сравнение, TTL
  10 минут, лимит попыток (5) действует именно на «текущий» (последний выданный) код за счёт
  `OrderByDescending(CreatedAt).FirstOrDefault` — старые невостребованные коды не создают
  дополнительных векторов брутфорса. Троттлинг выдачи — 3 активных в час на email + отдельно 3/час
  на IP.
- Хеширование пароля (`PasswordHasher`) — PBKDF2-SHA256, 210 000 итераций, constant-time verify,
  обратная совместимость со старым PIN-форматом учтена и задокументирована явно.
- `DevAuthenticationHandler` строго регистрируется только при `builder.Environment.IsDevelopment()`
  (`Program.cs:236-239`), и forward-selector в `AddPolicyScheme` тоже проверяет `isDevelopment`
  перед выбором dev-схемы — нет пути, которым dev-заголовок сработал бы в проде.
  `X-Dev-TelegramId` игнорируется бэкендом вне Development безусловно.
  Один нюанс на будущее: клиентский код (`telegram.service.ts:getDevTelegramId()`) читает и
  сохраняет `?devTgId=` в `localStorage` без проверки `environment.production` — безопасно (сервер
  всё равно отклонит в проде), но не идеально аккуратно; см. также [08-web-frontend].
- Ротация refresh-токенов с reuse-detection: предъявление уже отозванного (использованного)
  refresh-токена триггерит `RevokeAllForUserAsync` для всей цепочки — корректная защита от кражи
  refresh-токена через повторное воспроизведение.
- Lockout после 5 неудачных попыток входа на 15 минут, сброс счётчика при успехе — корректно.
- Анти-enumeration ответы (`register/start`, `reset-password/start`) — единообразные 200 вне
  зависимости от существования адреса; `TelegramBindingEndpoints`-аналоги тоже соблюдают паттерн.
- `AccountMergeService` — аккуратно переносит все FK-less связи по User.Id, порядок операций
  (удаление source ДО присвоения TelegramId target) верно учитывает уникальный индекс.
