# Аудит безопасности FamilyHub — 2026-10-08

Ручной аудит кода и конфигурации на `master` (`6a9aae1`). Продолжает
[module-review-2026-08-02](module-review-2026-08-02/00-INDEX.md): с тех пор вышло ~220 коммитов
(отчёты для врача, прививки, курсы лекарств и dose-actions, Web Push, админ-панель, превью вложений
через Gotenberg). Здесь — новое и то, что прошлый аудит не покрыл. Риски, уже осознанно принятые в
[threat-model.md](threat-model.md), только упоминаются со ссылкой.

Обозначения: 🔴 высокий → 🟡 средний → 🟢 низкий / defense-in-depth → ✅ проверено, проблем нет.
«Требует PoC» — уязвимость видна по коду и конфигурации, но на живом стенде не воспроизводилась.

## Сводка

| ID | Уровень | Кратко | Этап |
|---|---|---|---|
| H1 | 🔴 ✅ | SSRF / чтение внутренних ресурсов через конвертацию документов в Gotenberg (LibreOffice) — **исправлено** | 0 |
| H2 | 🔴 ✅ | Сброс пароля не отзывает действующие сессии — **исправлено** | 0 |
| M1 | 🟡 ✅ | Слепой SSRF через endpoint Web Push-подписки — **исправлено** | 1 |
| M2 | 🟡 ✅ | HTML/XML-вложения отдаются inline на основном origin — **исправлено** | 1 |
| M3 | 🟡 ✅ | Админ-панель отделена от публичного домена только фильтром путей Caddy — **исправлено** | 1 |
| M4 | 🟡 ✅ | CSRF-проверка не выполняется, если нет cookie `XSRF-TOKEN` (fail-open) — **исправлено** | 1 |
| M5 | 🟡 | Access-JWT действует до 15 мин после отзыва сессии | 2 |
| M6 | 🟡 ✅ | Telegram initData: без `auth_date` срок не проверяется, окно повтора 24 ч — **исправлено** | 1 |
| M7 | 🟡 | Цепочка поставки CI/CD и уязвимые dev-зависимости npm | 2 |
| L1–L8 | 🟢 | Токены в путях логов, доверие XFF, Kafka без аутентификации, сегментация сети и др. | 2–3 |

---

## 🔴 Высокий

### H1. SSRF / чтение внутренних ресурсов через конвертацию документов в Gotenberg (требует PoC)

**Где:** `src/FamilyHub.Infrastructure/Documents/DocumentContentTypes.cs:25,44` (в allow-list вложений
есть `text/html`, `application/xml`, `doc`/`docx`/`rtf`);
`src/FamilyHub.Infrastructure/Previews/AttachmentPreviewRenderer.cs:58-64` (Doc/Rtf/Html/Office
уходят в конвертацию); `src/FamilyHub.Infrastructure/Previews/GotenbergConverter.cs:37`
(`/forms/libreoffice/convert`); `deploy/docker-compose.prod.yml:108-126` (gotenberg в общей
docker-сети, egress не ограничен, тег `gotenberg/gotenberg:8` плавающий).

**Сценарий:** пользователь загружает к своей мед-записи HTML или DOCX, где есть внешние ресурсы:
`<img>`/`<iframe>` в HTML, связанные изображения и OLE-объекты, поля `INCLUDETEXT`/`INCLUDEPICTURE`
в DOCX/RTF. LibreOffice подтягивает их при конвертации. Запрос уходит из контейнера gotenberg в
docker-сеть и дальше: `api:8080`, `minio:9000/9001`, `seq`, LM Studio на ноутбуке за WireGuard (у него
нет аутентификации), метаданные VPS-провайдера (`169.254.169.254`). Полученное содержимое
встраивается в PDF-превью, а превью атакующий открывает сам. Получается **чтение** через SSRF,
а не только слепой запрос. Это противоречит и ADR-0001 (локальность медданных и egress-политика).

**Исправление:**
1. Вывести gotenberg в отдельную сеть `previews` с `internal: true`. В этой сети только `api` и
   `gotenberg`, у gotenberg не остаётся ни egress, ни доступа к остальным сервисам.
2. Не конвертировать `text/html`/`xml` через LibreOffice: показывать их как обычный текст
   (`AttachmentRenderKind.Text` уже есть). Либо убрать HTML/XML из allow-list загрузки.
3. Закрепить версию Gotenberg (тег + digest) и сверить её с известными CVE в LibreOffice и Chromium.
4. Опционально: оставить в Gotenberg только нужные маршруты (`--api-disable-*` / прокси-фильтр).

**Проверка:** PoC с canary-URL (HTML `<img src="http://canary.internal/x">` и DOCX с `INCLUDEPICTURE`)
до и после исправления. Интеграционный тест: при конвертации нет исходящих запросов.

**✅ Исправлено (ветка `security`):**
- HTML больше не уходит в LibreOffice: `AttachmentPreviewRenderer.IsOfficeRoute` его не включает, а
  `GotenbergConverter` не знает расширения `html`. Вьюер, как и раньше, показывает HTML как текст.
  Регрессионный unit-тест: `AttachmentPreviewRendererTests.RenderAsync_Html_DoesNotCallGotenberg`.
- В проде gotenberg вынесен в отдельную сеть `previews` с `internal: true`
  (`deploy/docker-compose.prod.yml`). `api` подключён к `default` и `previews`, у gotenberg нет ни
  egress, ни доступа к postgres/minio/seq/kafka, LM Studio и метаданным VPS.
- Образ закреплён на `gotenberg/gotenberg:8.37.0` (прод и дев).
- Остаток: DOC/DOCX/RTF по-прежнему разбирает LibreOffice, но без сети. Это закрывает SSRF, но не
  уязвимости самого парсера, поэтому версию Gotenberg нужно регулярно обновлять (см. M7).
  Ручной PoC на живом стенде не проводился.

### H2. Сброс пароля не отзывает действующие сессии

**Где:** `src/FamilyHub.Api/Features/Auth/AuthEndpoints.cs:95-109` (`/reset-password/confirm`) →
`src/FamilyHub.Api/Features/Auth/PwaAuthService.cs:203-227`. Для сравнения, `/change-password`
отзывает сессии (`AuthEndpoints.cs:213`, `RevokeAllForUserAsync`).

**Сценарий:** злоумышленник получил refresh-токен (украденная cookie, чужое устройство). Жертва
замечает это и сбрасывает пароль через «Забыли пароль?». Сессия злоумышленника продолжает
ротироваться и живёт до `RefreshTokenLifetime` (14 дней). Сброс пароля — главный способ
восстановления доступа, но доступ не отбирает.

**Исправление:** в `/reset-password/confirm` после успешного `ConfirmResetPasswordAsync` и до
`IssueSessionAsync` вызвать `await tokenService.RevokeAllForUserAsync(userId, ct)`. На этот же путь
стоит поставить письмо «пароль изменён».

**Проверка:** интеграционный тест в `PwaAuthFlowTests`: refresh-токен, выданный до сброса, после
сброса получает 401, а новая сессия работает.

**✅ Исправлено (ветка `security`):** `/reset-password/confirm` вызывает `RevokeAllForUserAsync` перед
выпуском новой сессии. Регрессионный интеграционный тест:
`JwtSessionTests.ResetPassword_RevokesExistingSessions_AndIssuesWorkingNewOne`. Письмо «пароль
изменён» пока не добавлено, а access-токен живёт до 15 минут — это остаётся в M5.

---

## 🟡 Средний

### M1. Слепой SSRF через endpoint Web Push-подписки

**Где:** `src/FamilyHub.Api/Features/Push/PushEndpoints.cs:18-23`,
`src/FamilyHub.Api/Features/Push/PushSubscriptionService.cs` (`SubscribeAsync`: значение `Endpoint`
сохраняется без проверки); `src/FamilyHub.Infrastructure/Notifications/WebPushNotificationSender.cs:62-63`
(POST на сохранённый URL).

**Сценарий:** любой аутентифицированный пользователь подписывается с
`endpoint = "http://seq/api/events/raw"` (`http://minio:9000/…`, `http://10.8.0.x:1234/…` и т.п.).
При каждом оповещении API отправляет POST на внутренний адрес. Тело запроса зашифровано и
атакующему не подконтрольно, поэтому это слепой SSRF: сканирование внутренней сети и сервисов
по времени ответа и кодам 404/410 (по ним подписка удаляется), обход egress-allowlist из ADR-0004.

**Исправление:** валидировать endpoint при подписке: только `https`, хост из allow-list ADR-0004
(`fcm.googleapis.com`, `*.googleapis.com`, `updates.push.services.mozilla.com`, `web.push.apple.com`,
`*.notify.windows.com`), без IP-литералов и нестандартных портов. Ограничить длину
`Endpoint`/`P256dh`/`Auth` и число подписок на пользователя (≈10). Для защиты от DNS-rebinding
дополнительно запретить приватные адреса в `SocketsHttpHandler.ConnectCallback` HTTP-клиента Web Push.

**Проверка:** unit-тесты валидатора (http, IP, `localhost`, имя сервиса docker, допустимые хосты)
и интеграционный тест: 400 на недопустимый endpoint.

**✅ Исправлено (ветка `security`):**
- `PushEndpointPolicy` (`src/FamilyHub.Infrastructure/Notifications/`): только `https`, стандартный порт,
  без userinfo и IP-литералов, хост из allow-list ADR-0004; ключи — base64url до 256 символов.
  `/api/push/subscribe` на остальное отвечает `400 invalid_push_subscription`.
- `WebPushNotificationSender` не отправляет на недопустимые endpoint, сохранённые до исправления,
  и удаляет такие подписки.
- Не больше 10 подписок на пользователя: при новой сверх лимита вытесняется самая давно
  не использовавшаяся (закрывает и L7 по количеству).
- Тесты: `PushEndpointPolicyTests`, `WebPushNotificationSenderTests.SendAsync_NonPushRelayEndpoint_IsNotCalled_AndRemoved`,
  `PushApiTests.Subscribe_NonPushRelayEndpoint_Returns400`.
- Не сделано: запрет приватных адресов в `ConnectCallback`. Allow-list состоит из доменов Google,
  Mozilla, Apple и Microsoft, DNS-rebinding через них не реален.

### M2. HTML/XML-вложения отдаются inline на основном origin

**Где:** `src/FamilyHub.Modules.Medical/Attachments/AttachmentEndpoints.cs:108-122` (`/inline`:
`Results.Stream` с сохранённым `ContentType`, без `Content-Disposition`). Тип файла заявляет клиент
и он не сверяется с содержимым (`AttachmentService.cs:51-74`).

**Сценарий:** вложение `text/html` открывается по подписанной ссылке на том же origin, что и SPA.
Общий CSP (`script-src 'self'`) блокирует inline-JS, но HTML-инъекции, поддельные формы входа
(`form-action 'self'`), CSS-эксфильтрация (`style-src 'unsafe-inline'`) и подключение same-origin
скриптов остаются. Ссылку можно подсунуть члену семьи, которому открыт доступ к записи.

**Исправление:** для всего из `DocumentContentTypes.PlainTextLike` в `/inline` отдавать
`text/plain; charset=utf-8`. На все ответы `/api/attachments/*/(file|inline|preview/*)` добавить
`Content-Security-Policy: sandbox; default-src 'none'` и `X-Content-Type-Options: nosniff`.
При загрузке сверять magic bytes для PDF и изображений с заявленным типом.

**Проверка:** интеграционный тест: `/inline` для HTML-вложения возвращает `text/plain` и заголовок
`sandbox`.

**✅ Исправлено (ветка `security`):**
- Все анонимные эндпоинты байт вложений (`file`, `inline`, `preview/thumb|pdf|page`) отдают
  `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox` и `nosniff`
  вместо CSP приложения (фильтр `HardenFileResponse` в `AttachmentEndpoints.cs`).
- `/inline` для `PlainTextLike` (HTML, XML, CSV, TXT, RTF) отдаёт `text/plain; charset=utf-8`.
  Просмотрщик это не задевает: вид текста он берёт из `ContentType` в `/preview`, а PDF и текст
  загружает через `fetch` (pdf.js).
- Тест: `AttachmentsApiTests.HtmlAttachment_ServedInlineAsPlainText_InSandbox`.
- Не сделано: сверка magic bytes. С `nosniff` и sandbox-CSP подмена типа уже не даёт исполнения,
  а проверка сломала бы около 7 интеграционных тестов, которые загружают текст как `application/pdf`.
  Остаётся в бэклоге как защита от мусорных загрузок.

### M3. Админ-панель отделена от публичного домена только фильтром путей Caddy

**Где:** `src/FamilyHub.Api/Features/Admin/AdminEndpoints.cs:14`, `AdminSessionEndpoints.cs`,
`src/FamilyHub.Api/Security/AdminSessionCookie.cs`, `deploy/Caddyfile:40`.

**Суть:** `/api/admin/*` обслуживает тот же `api:8080`, что и публичный сайт, и само приложение не
проверяет ни Host, ни порт. Публичный домен закрывает только `@blocked path` в Caddy: любая ошибка
в Caddyfile, новый маршрут вне `/api/admin/` или иной прокси сразу открывают панель в интернет.
Сама панель держится на одной общей паре логин/пароль без 2FA, rate-limit на вход только по IP
(`auth`), cookie-сессия без серверного состояния (выход и отзыв не инвалидируют выданный токен
до истечения `SessionLifetime`). При этом панель ротирует учётки Postgres/MinIO и ключи шифрования.

**Исправление:**
1. Повесить `RequireHost("admin.{PUBLIC_DOMAIN}:4059")` (значение из конфигурации) на все
   admin-группы, включая `/api/admin/session`.
2. Блокировка после N неудачных входов (глобальная, не по IP) и аудит входов и выходов.
3. Серверный реестр админ-сессий (id в cookie, запись в БД), чтобы выход и «выйти везде»
   действительно отзывали сессию.
4. Бэклог: TOTP для входа в панель.

**Проверка:** интеграционный тест: запрос `/api/admin/*` с чужим Host возвращает 404.

**✅ Исправлено (ветка `security`):**
- `Admin:AllowedHosts` + `AdminHostGuardMiddleware`: на чужом Host `/api/admin/*` и `/admin*`
  отвечают 404 до аутентификации и SPA-fallback. Прод задаёт `admin.${PUBLIC_DOMAIN}:4059`
  (`deploy/docker-compose.prod.yml`). Если список пуст (дев, тесты), ограничения нет и на старте
  пишется предупреждение.
- `AdminLoginThrottle`: после `Admin:MaxFailedLogins` (10) неудач подряд вход блокируется на
  `Admin:LockoutDuration` (15 мин) глобально, ответ `429 admin_locked`, пароль в это время
  не проверяется. Форма входа показывает понятное сообщение.
- Cookie сессии несёт случайный id. `DELETE /api/admin/session` отзывает его на сервере
  (`AdminSessionRevocations`), и скопированная cookie больше не работает. Реестр хранится в памяти:
  после рестарта API отозванная сессия снова действительна до конца своего `SessionLifetime`
  (≤ 12 ч). Это осознанный компромисс при одном инстансе, вместо таблицы и миграции.
- Аудит-логи в Seq: успешный и неудачный вход, блокировка, выход, с IP.
- Тесты: `AdminLoginThrottleTests`, `AdminHostGuardMiddlewareTests`, `AdminHostGuardApiTests`,
  `AdminSessionApiTests.Logout_RevokesSessionServerSide_CopiedCookieNoLongerWorks`.
- Не сделано: TOTP и «выйти везде» (бэклог, этап 3).

### M4. CSRF-проверка не выполняется, если нет cookie `XSRF-TOKEN` (fail-open)

**Где:** `src/FamilyHub.Api/Startup/CsrfGateMiddleware.cs:54-57`: заголовок проверяется, только если
в запросе есть публичная cookie `CsrfCookieNames.PublicToken`.

**Сценарий:** cookie access-токена есть, а CSRF-cookie нет: истекла раньше (выдаётся на
`AccessTokenLifetime`, `AuthEndpoints.cs:172`), вытеснена или перезаписана с same-site поддомена
(`seq.`, `s3.`, `admin.` — same-site, SameSite=Lax от них не защищает). Тогда мутирующий запрос
проходит вообще без проверки. Защита держится на присутствии cookie, а его косвенно может
устранить атакующий.

**Исправление:** для мутирующего `/api`-запроса с PWA-identity всегда вызывать
`IAntiforgery.IsRequestValidAsync`, а при отсутствии cookie или заголовка возвращать 400. Перевыпускать
CSRF-cookie при каждом refresh. Для сессионных cookie по возможности взять префикс `__Host-`.

**Проверка:** тест: PWA-запрос POST без CSRF-cookie и без заголовка получает 400.

**✅ Исправлено (ветка `security`):**
- `CsrfGateMiddleware` проверяет `IAntiforgery` у каждого мутирующего `/api`-запроса с identity
  `PwaCookie`, есть cookie `XSRF-TOKEN` или нет.
- Публичная CSRF-cookie стала сессионной, как и приватная половина. Раньше она протухала через
  `AccessTokenLifetime` после `/me` и не перевыпускалась на `/refresh`, а с fail-closed гейтом это
  ломало бы живые вкладки. Перевыпуск на `/refresh` невозможен: этот запрос анонимный, а токен
  привязывается к identity.
- Фронт (`auth.interceptor.ts`): на `400 csrf_token_invalid` один раз вызывает `GET /api/auth/me`
  (выдаёт свежую пару) и повторяет запрос через полный HTTP-конвейер, чтобы заголовок подставил
  xsrf-интерцептор Angular.
- Тесты: `JwtSessionTests.MutatingPwaRequest_WithoutAnyCsrfCookie_Returns400` и `…_WithCsrfPair_Passes`.
  Logout-тесты и e2e `18-pwa-password-reset` теперь шлют CSRF-пару.
- Не сделано: префикс `__Host-` для cookie. Смена имён разлогинила бы всех пользователей,
  поэтому вынесено в бэклог.

### M5. Access-JWT действует до 15 минут после отзыва сессии

Открыто с [01-auth-identity №2](module-review-2026-08-02/01-auth-identity.md). Касается выхода,
«завершить сеанс», смены и сброса пароля (H2) и удаления аккаунта.
**Исправление:** в `JwtBearerEvents.OnTokenValidated` проверять claim `SessionId` по списку отозванных
сессий (`IMemoryCache` с TTL ≈ `AccessTokenLifetime`, промах кэша — запрос в БД) или сократить
`AccessTokenLifetime` до 5 минут.

### M6. Telegram initData: без `auth_date` срок не проверяется, окно повтора 24 ч

**Где:** `src/FamilyHub.Infrastructure/Telegram/TelegramInitDataValidator.cs:66`: TTL проверяется только
внутри `if (long.TryParse(pairs["auth_date"], …))`, поэтому подписанные данные без `auth_date`
действуют бессрочно. `TelegramOptions.MaxInitDataAge` равен 24 ч (`TelegramOptions.cs:17`).
initData, перехваченные один раз (логи, расширение браузера, скриншот devtools), дают сутки доступа.
Выход и отзыв сессий на Telegram-доступ не влияют.

**Исправление:** без валидного `auth_date` отклонять. Отклонять `auth_date` из будущего с запасом
больше ~1 мин. Снизить `MaxInitDataAge` до ~1 ч: Mini App получает свежие initData при каждом
открытии. Unit-тесты на оба случая.

**✅ Исправлено (ветка `security`):**
- `TelegramInitDataValidator.Check`: без валидного `auth_date` и с `auth_date` из будущего
  (больше 5 мин) — `Invalid`, старше `MaxInitDataAge` — `Expired`. Срок проверяется после подписи.
- `Telegram:MaxInitDataAge`: 24 ч → **1 ч** (решение владельца продукта).
- На просроченные initData `TelegramMiniAppAuthenticationHandler` отвечает
  `401 {code: "init_data_expired"}`. Фронт показывает экран `/telegram-expired` («закройте и откройте
  приложение заново») вместо повторной привязки почты.
- Тесты: `TelegramInitDataValidatorTests` (без `auth_date`, мусор, будущее, просрочка, подделка),
  `TelegramMiniAppAuthenticationHandlerTests.ExpiredInitData_…`.
- Остаток: в пределах часа перехваченные initData всё ещё можно использовать повторно.
  Выход из PWA на Telegram-доступ не влияет, это следствие модели Telegram.

### M7. Цепочка поставки CI/CD и зависимости

- GitHub Actions закреплены изменяемыми тегами (`@v4`, `@v3`), в том числе сторонний
  `webfactory/ssh-agent@v0.9.0` (`.github/workflows/deploy.yml:71`) в job, где есть SSH-ключ прода
  и `PROD_ENV`. → Закрепить по SHA, включить Dependabot для `github-actions`.
- Плавающие теги образов: `datalust/seq:latest`, `caddy:2-alpine` (`gotenberg` закреплён на
  `8.37.0` в рамках H1). Деплой берёт `IMAGE_TAG=latest` без digest
  (`deploy.yml:97`). → Закрепить версии и digest, деплоить по digest собранного образа.
- В `ci.yml` и `deploy.yml` не задан явный `permissions:`. → Указать `permissions: contents: read`
  (и `packages: read` там, где нужно).
- `docker login ghcr.io` на VPS (`deploy.yml`, шаг `docker compose pull`) сохраняет токен в
  `~/.docker/config.json`. → Делать `docker logout ghcr.io` после pull.
- **npm (FamilyHub.Web), 2026-10-08:** `npm audit --omit=dev` и `e2e` чистые. Полный `npm audit`
  (вместе с dev и сборочными зависимостями) показывает **9 уязвимостей (4 critical, 4 high,
  1 moderate)**: `piscina`, `tinypool` (prototype pollution → RCE), `http-cache-semantics`,
  `source-map-js`, `@modelcontextprotocol/sdk`, `@vitest/mocker`. В прод-бандл они не попадают,
  но выполняются при сборке в CI. → `npm audit fix` и проверка сборки/тестов.
- **NuGet:** `dotnet list package --vulnerable --include-transitive` в этом аудите не запускался
  (в окружении нет .NET SDK). → Запустить локально и добавить шаг в `ci.yml`.

---

## 🟢 Низкий / defense-in-depth

- **L1. Секретные токены в путях URL попадают в логи.** Serilog request logging пишет `{RequestPath}`
  (`src/FamilyHub.Api/Startup/RequestLoggingRegistration.cs:20`) в Seq, а Caddy — в свой access-лог.
  Так в журналы попадают токены `/api/public/doctor-reports/{token}` и
  `/api/public/dose-actions/{token}` (`DoseActionEndpoints.cs:16`, к тому же это state-changing
  GET, который могут вызвать префетчеры). → Маскировать сегмент токена в `EnrichDiagnosticContext`
  или `MessageTemplate`; dose-action перевести на POST (или на подтверждение в SW).
- **L2. Gotenberg Chromium (отчёт для врача).** JS выключен (`--chromium-disable-javascript`),
  пользовательский текст экранируется (`DoctorReportHtmlRenderer.E`), но нет `--chromium-deny-list`
  и сетевой изоляции. Если где-то пропустить экранирование, получится SSRF. Закрывается вместе с H1.
- **L3. Доверие `X-Forwarded-*` от всей `172.16.0.0/12`** (`ProxyHeadersMiddleware.cs:22`): любой
  контейнер docker-сети подделает IP клиента (обход rate-limit) и схему. → Доверять только
  IP или подсети Caddy (фиксированная подсеть сети `frontend`).
- **L4. Kafka в режиме PLAINTEXT без аутентификации.** Любой скомпрометированный контейнер может
  писать в `telegram-outbound` (рассылать сообщения от имени бота). → Изолировать сетью; SASL/ACL
  по мере роста.
- **L5. Docker-сети прода не сегментированы**: все сервисы в default-сети, что усиливает H1, M1, L3
  и L4. → Сети `frontend` (caddy↔api, wg-client), `backend` (api↔postgres/minio/kafka/seq),
  `previews` (`internal: true`, api↔gotenberg).
- **L6. `/change-password`: неверный текущий пароль не увеличивает `FailedLoginAttempts`**
  (`PwaAuthService.cs:246`). При угнанной сессии пароль подбирается, тормозит только rate-limit
  `auth-session`. → Тот же счётчик и блокировка, что при входе.
- **L7. Push-подписка:** upsert по `EndpointHash` переносит владение подпиской, нет лимитов количества
  и длины полей (закрывается вместе с M1).
- **L8. «Отравление» общего справочника:** веб-поиск и LLM-суммаризация пишут в KB, который видят все
  пользователи. → Держать review-гейт (`EnrichmentReviewConfig`) включённым для новых записей и
  показывать источник.

---

## ✅ Проверено, проблем не найдено

- Сырой SQL только из статических строк (`AdminStatsService`, `LabAnalyteKbRebuildJob`), без
  пользовательского ввода. Остальное — EF LINQ.
- Подписанные ссылки на вложения: HMAC по `id:scope:expires`, TTL 5 мин, сравнение за постоянное
  время, ротация ключей. Блобы хранятся зашифрованными (AES-GCM).
- Telegram: HMAC initData за постоянное время, отказ при пустом BotToken. Вебхук бота проверяет
  `X-Telegram-Bot-Api-Secret-Token` (fail-closed), `X-Internal-Token` закрыт так же.
- Заголовки безопасности: CSP, `nosniff`, `frame-ancestors 'none'`/XFO, HSTS за прокси, Referrer-Policy.
- Angular: `bypassSecurityTrustHtml` только для встроенных юридических текстов (инвариант
  задокументирован), dev-Telegram-ID отключён `isDevMode()`. `npm audit --omit=dev` чистый.
- Dev-аутентификация регистрируется только при `DevTools:DevAuthEnabled`. Fail-fast при пустых
  `Admin`/`DevTools`-учётках. Сравнение учётных данных за постоянное время (`CredentialComparer`).
- Токены (инвайты, отчёты, dose-actions, коды) генерируются CSPRNG, в БД хранится только SHA-256.
  Токен отчёта дополнительно лежит в `[Encrypted]`-поле.
- Отчёт для врача: весь пользовательский текст в HTML экранируется, Chromium без JS.
  Резолверы субъектов (`DoctorReportSubjects`, `VaccinationSubjects`) проверяют активное членство
  в семье и `HealthShareGrant`.
- CI: секреты `PROD_ENV`/`WG_CONF` передаются через `env:`, а не подстановкой `${{ }}` в скрипт.
  `known_hosts` задаётся явно.

---

## План устранения

| Этап | Срок | Задачи | Критерий готовности |
|---|---|---|---|
| **0. Немедленно** ✅ | 1–2 дня | **H2**: `RevokeAllForUserAsync` в reset + тест. **H1**: PoC; сеть `previews` (`internal: true`) для gotenberg; HTML/XML показывать как текст; закрепить версию Gotenberg | Тест H2 зелёный; PoC H1 после исправления не даёт исходящих запросов |
| **1. Ближайший спринт** ✅ | ≤ 2 нед | **M1** валидатор push-endpoint и лимиты; **M2** `text/plain` + `CSP: sandbox` для вложений, magic bytes; **M3** `RequireHost` + блокировка + аудит входов; **M4** CSRF без fail-open; **M6** обязательный `auth_date`, TTL ≈1 ч | Интеграционные и unit-тесты на каждый пункт; `CaddyfileTests` не сломаны |
| **2. Следующий спринт** | ≤ 1 мес | **M5** проверка `SessionId` в `OnTokenValidated`; **M7** pin SHA/digest, `permissions:`, Dependabot, `npm audit fix`, `dotnet list package --vulnerable` в CI; **L3/L5** сегментация сетей и доверие XFF только от Caddy; **L4** изоляция Kafka | В CI есть шаг скана зависимостей; `docker compose config` с новыми сетями; деплой по digest |
| **3. Бэклог** | — | **L1** маскирование токенов в логах, POST для dose-action; **L2** deny-list Chromium; **L6** блокировка при change-password; **L7**; **L8** review-гейт; TOTP и серверные сессии для админки | — |

После закрытия каждого этапа: отметить находки здесь как `✅ Исправлено (коммит/PR)` и обновить
[threat-model.md](threat-model.md) и [access-matrix.md](access-matrix.md).
