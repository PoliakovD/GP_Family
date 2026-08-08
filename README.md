# FamilyHub — Семейное приложение

## 📋 Суть проекта

**FamilyHub** — семейное приложение для хранения и шаринга медицинских данных, аптечки, дней
рождения с разграничением доступа по семьям и ролям. Два клиента на одном API: **PWA/Angular**
(email + пароль) и **Telegram Mini App** (тонкий клиент, привязывается к тому же аккаунту).

### Основные фичи
- **Аптечка** (медкиты + медикаменты) — лекарства, инструкции, сроки годности (с оповещениями),
  распознавание по фото (OCR через локальную LLM)
- **Справочник препаратов** — общий обезличенный каталог, автообогащается веб-поиском +
  суммаризацией локальной моделью, когда препарата ещё нет в базе
- **Мед-анализы** — записи по датам, врачам, описанию + PDF/фото-сканы. Персональные, с
  двухуровневым управляемым шарингом на семью
- **Дни рождения** членов семьи, с виджетом ближайших на главном экране
- **Глобальный поиск** — по лекарствам, справочнику, анализам (Postgres full-text, русская морфология)
- **Оповещения** — Telegram-бот и/или Web Push (сроки годности лекарств, дни рождения)
- **Настройки/аккаунт** — смена пароля, список активных сессий, привязка/отвязка Telegram,
  уведомления по типам, экспорт и удаление всех данных (152-ФЗ)

### Пользовательская модель
Пользователь может состоять в нескольких семьях одновременно с разными ролями. Разграничение
доступа — центральная часть проекта. Identity — **email как единственный якорь**: у каждого
пользователя ровно один email/пароль, Telegram — дополнительный привязанный канал входа
(подробнее — раздел «Аутентификация»).

---

## 🏗 Архитектура

### Модульный монолит

```
GP_Family.slnx
├── FamilyHub.Api               // composition root (Program.cs), auth/families/invites/
│                                //   members/consents/account/push/bot-webhook, раздача Angular SPA
├── FamilyHub.Contracts         // DTO/доменные события, общие между Api и модулями (шина — MassTransit)
├── FamilyHub.Domain            // сущности, enum'ы, value objects, интерфейсы (IFamilyOwned)
├── FamilyHub.Infrastructure    // EF Core, auth (JWT + Telegram), авторизация, шифрование,
│                                //   файловое хранилище (MinIO/Local), Hangfire-оповещения,
│                                //   email, LM Studio, поиск, событийная шина (MassTransit), аудит
├── FamilyHub.Modules.Medical   // аптечка, анализы, вложения, OCR, справочник + AI-обогащение
├── FamilyHub.Modules.Birthdays // дни рождения
└── FamilyHub.Web               // Angular 18 PWA — единственный фронтенд, обслуживает и браузер,
                                 //   и Telegram Mini App (тот же билд, разное поведение по контексту)
```

Отдельного проекта `FamilyHub.TelegramBot` нет — обработка апдейтов бота живёт в
`FamilyHub.Api/Features/Bot` + `FamilyHub.Infrastructure/Telegram`, бот встроен в тот же процесс,
что и API.

**Принцип:** ядро (API + модель доступа + данные) — одно, клиенты (PWA, Telegram Mini App, бот)
подключаются к нему поверх общего контракта.

### Зависимости модулей

- `*.Modules.*` и `FamilyHub.Api` зависят от `FamilyHub.Domain`, `FamilyHub.Infrastructure` и
  `FamilyHub.Contracts`, но **никогда** друг от друга напрямую (Medical не знает о Birthdays).
- Общие сквозные сервисы (доступ по ролям, текущий пользователь, хранилище файлов, шифрование,
  оповещения, событийная шина) живут в Infrastructure как абстракции, подключаются модулям через DI.
- Каждый модуль — свой csproj с `AddXModule()`/`MapXModule()`, регистрируется в `Program.cs`.

---

## 🎯 Модель доступа (ядро задачи)

### Два РАЗНЫХ типа владения

| Ресурс | Владелец | Кто видит | Кто управляет доступом |
|---|---|---|---|
| Аптечка (медкиты, медикаменты), ДР | **Семья** (`FamilyId`) | Все активные члены семьи (по роли) | Админ семьи |
| Мед-анализы | **Пользователь** (`OwnerUserId`) | Владелец + семьи, где расшарено и не скрыто | **Только владелец** |

### Роли в семье
Две роли: `Member` (просмотр + добавление/правка семейных ресурсов) и `Admin` (то же + приглашения
и удаление участников). Понижения нет — только выгнать или выйти самому.

---

## 🔐 Аутентификация — email как единственный якорь identity

Никакого merge аккаунтов по умолчанию: у пользователя один email/пароль, Telegram — опциональный
привязанный канал. Полная история решения — `.claude/research/auth-architecture-email-anchor.md`
(память), реализация — `.claude/research/auth-email-anchor-jwt-rework.md`.

- **PWA (браузер)** — регистрация/вход email + пароль (`PasswordRules`: 8+ симв., строчная +
  заглавная + цифра). Сессия — access-**JWT** (короткоживущий, httpOnly cookie) + DB-backed
  refresh-токен (`UserSession`) с ротацией и **reuse-detection**: если предъявлен уже
  использованный refresh-токен — все сессии пользователя отзываются разом (признак кражи токена).
- **Telegram Mini App** — **lookup-only**: `TelegramMiniAppAuthenticationHandler` проверяет HMAC
  подписанного `initData` и ищет пользователя по `TelegramId`; если не привязан — 401, никакого
  авто-создания аккаунта. Привязка — отдельный флоу (`/api/auth/telegram/{init,send-code,bind,revoke}`):
  email + одноразовый код на почту роднит `TelegramId` с существующим или новым `User`.
- **Dev-режим** — `DevAuthenticationHandler` (заголовок `X-Dev-TelegramId`), регистрируется
  только при `ASPNETCORE_ENVIRONMENT=Development`, структурно недоступен в проде.
- Резервный сброс пароля, смена пароля, список сессий с ручным отзывом (`logout-all`) — в
  `/settings`.

---

## 🗄 Схема БД (основные сущности)

```
User, UserSession, EmailVerificationCode, TelegramLinkCode, UserConsent, UserNotificationPreference
Family, FamilyMember, FamilyInvite, FamilyInviteRedemption

Medkit                  -- аптечка (контейнер, семейный ресурс)
Medication               -- медикамент внутри медкита, ExpiryDate для оповещений
GlobalMedicationKb        -- обезличенный общий справочник препаратов (без персональных данных)
MedicationEnrichmentJob   -- фоновая задача обогащения справочника (Hangfire)
MedicationSearchCache     -- кеш результатов веб-поиска по препарату

MedicalRecord             -- анализ или посещение врача (Kind), персональный ресурс (OwnerUserId)
FamilyMedicalShare         -- уровень 1: владелец открыл все свои записи семье (общий для обоих Kind)
MedicalRecordHidden        -- уровень 2: точечное скрытие записи от конкретной семьи
FileAttachment              -- метаданные вложений; сами файлы в MinIO, шифрованы, ключ обезличен
MedicalAccessAudit          -- аудит-лог доступа к чужим медданным
PersonalCompatibilityResult -- результат персонального анализа совместимости (справочник)

Birthday                  -- семейный ресурс

Notification               -- оповещение, UNIQUE(DedupKey) для идемпотентности
PushSubscription            -- Web Push подписка (Endpoint/keys шифрованы, EndpointHash для lookup)
```

Персональные и медицинские поля шифруются at-rest (AES-GCM, см. раздел «Безопасность»); полный
список сущностей — `src/FamilyHub.Domain/Entities`, EF-конфигурации — `Infrastructure/Persistence/Configurations`.

---

## 🔐 Логика видимости мед-записей (анализы и посещения врачей)

```csharp
// Видно, если: владелец, ИЛИ (мои записи расшарены этой семье
// И я в ней состою И запись не скрыта именно от неё). Одинаково для обоих Kind.
var visibleRecords = _db.MedicalRecords
    .Where(r =>
        r.OwnerUserId == userId
        || _db.FamilyMedicalShares.Any(share =>
                share.OwnerUserId == r.OwnerUserId &&
                _db.FamilyMembers.Any(m =>
                    m.FamilyId == share.FamilyId &&
                    m.UserId == userId &&
                    m.Status == MemberStatus.Active) &&
                !_db.MedicalRecordsHidden.Any(h =>
                    h.MedicalRecordId == r.Id &&
                    h.FamilyId == share.FamilyId)));
```

Управление шарингом и скрытием — **только владелец записи**, даже админ семьи не может.

---

## 🔐 Безопасность

- **At-rest шифрование (152-ФЗ)** — `IFieldCipher`/`IFileCipher` (AES-GCM), синглтоны, ключ
  (`Encryption:MasterKey`) обязателен на старте (fail-fast, никакого дефолта в коде/compose).
  Формат `enc:{keyId}:{payload}` уже готов к ротации ключа (сама процедура ротации — не
  реализована, задокументировано как осознанный технический долг).
- **Файлы** — `IFileStorage`: MinIO, единственная реализация (в т.ч. для разработки —
  `LocalFileStorage` упразднён). Блоб зашифрован целиком, ключ объекта в бакете полностью
  обезличен (`StorageKeyFactory`). Скачивание — только через собственный API-эндпоинт с
  расшифровкой по короткоживущей HMAC-подписанной ссылке; presigned-ссылки самого хранилища
  упразднены (ADR-0002) — они бы отдавали шифротекст напрямую.
- **Resource-based authorization** — `IFamilyAccessService.HasRoleAsync` перед каждой операцией
  над семейным ресурсом; `FamilyRoleHandler : AuthorizationHandler<FamilyRoleRequirement, IFamilyOwned>`
  доступен как декларативная альтернатива. `FallbackPolicy` требует аутентификации на любом
  непокрытом явной политикой маршруте.
- **Согласия (152-ФЗ)** — `ConsentRequiredFilter` блокирует Medical/Birthdays-модули до принятия
  актуальной версии политики (`/api/consents`), тексты — `Api/Legal/*.html`.
- **Аудит** — `MedicalAccessAudit` логирует доступ к чужим медданным.
- **Событийная шина (MassTransit 8.5.1 + EF Core Outbox + Kafka Rider)** — изоляция модулей друг
  от друга через доменные события (`FamilyHub.Contracts/Events`, публикация — только через
  `IDomainEventPublisher`, `src/FamilyHub.Infrastructure/Messaging`); транзакционная запись в
  outbox поверх той же БД/транзакции, что и бизнес-запись, доставка — в реальный Kafka-топик
  (`Messaging:Kafka:Enabled=true`, дефолт для docker-compose/прода), на который подписаны все
  бизнес-потребители — задел под вынос модулей в микросервисы без переписывания контрактов/
  потребителей уже сегодня, не только "в будущем" (ADR-0006, ADR-0007). `Enabled=false`
  (дефолт `appsettings.json`, юнит-тесты, casual IDE-запуск) — dev-lite-режим на InMemory без брокера.
- **Rate limiting** — на auth-эндпоинтах (`RequireRateLimiting("auth")`).
- **Egress-политика** — данные по умолчанию не покидают РФ-контур; осознанные исключения
  задокументированы ADR'ами (Web Push через иностранные push-релеи — только зашифрованный
  RFC8291-payload; обогащение справочника — веб-поиск по обезличенному названию препарата).
- Полная модель угроз, матрица доступа и журнал модульного аудита безопасности —
  `docs/security/` (см. «Документация» ниже).

---

## 📬 Приглашения и участники

- **Персональный инвайт** (`TargetUserId` задан) → вступление сразу `Active`.
- **Ссылка-инвайт** → `PendingApproval`, пока админ не подтвердит; в этом статусе пользователь
  не видит вообще ничего в семье.
- Инвайт создаёт/одобряет/отклоняет только **Admin**; выгнать может Admin, выйти — любой сам;
  последнего админа удалить нельзя.
- При выходе/удалении из семьи автоматически чистится `FamilyMedicalShare` ушедшего.
- Вступление по инвайту **никогда** не открывает чужие анализы — это отдельный барьер
  (владельческий шаринг), не связанный со статусом членства.

---

## 🖥 Инфраструктура и деплой

- **Backend**: ASP.NET Core, PostgreSQL (EF Core), MinIO (S3-совместимое хранилище PDF/фото),
  Seq (структурные логи, Serilog), Hangfire на PostgreSQL (фоновые задачи и очереди, включая
  выделенную очередь `enrichment`).
- **Frontend**: Angular dev-server (`ng serve`) в контейнере с hot-reload через bind-mount;
  production-сборка (`ng build`) собирается отдельно и раздаётся тем же `FamilyHub.Api`.
- Локальный dev-стек — `docker-compose.yml`: сервисы `postgres`, `minio`, `seq`, `kafka`
  (`apache/kafka` KRaft — реальный транспорт бизнес-событий, ADR-0006/ADR-0007;
  `KAFKA_ENABLED=false` в `.env` переключает `api` на dev-lite InMemory-режим без брокера), `api`, `web`.
  Все секреты — через `.env` (см. `.env.example`), без дефолтов в compose для чувствительных
  ключей (`ENCRYPTION_MASTER_KEY` и т.п. — контейнер не стартует без них).
- Команды — `Makefile` (`make dev`, `make dev-restart`, `make dev-npm`, `make dev-rebuild`,
  `make logs[-web|-api]`) и `dev.ps1` для Windows/PowerShell.

---

## 🔐 Telegram Mini App

1. **Валидация `initData`** — HMAC с токеном бота, первым шагом, до бизнес-логики. Без
   `Telegram:BotToken` валидатор всегда отказывает.
2. Mini App — **lookup-only** (см. «Аутентификация»): непривязанный `TelegramId` получает 401 и
   ведётся на флоу привязки email-кодом, а не авто-регистрируется.
3. Контекст текущей семьи — почти каждый запрос к семейным ресурсам несёт `familyId`, членство
   проверяется на каждый вызов.
4. PDF/фото-сканы — только через собственный API-эндпоинт с HMAC-подписанной короткоживущей
   ссылкой, после проверки доступа (presigned-ссылки самого хранилища упразднены, ADR-0002).

---

## ⚖️ Правовая заметка

Хранятся чужие медицинские данные — спецкатегория персональных данных по 152-ФЗ. Реализовано:
at-rest шифрование персональных и медицинских полей, экспорт и полное удаление данных по запросу
пользователя (`/api/account/export`, `/api/account/delete`), явное согласие с политикой
(`/api/consents`) перед доступом к медицинским модулям, локализация данных в РФ-контуре с
задокументированными точечными исключениями (ADR-0001, ADR-0004, ADR-0005).

---

## 📁 Документация

### Ресёрч по архитектуре — `.claude/research/`
Актуальное «как сделано» по каждому модулю (индекс — `.claude/research/README.md`):
`domain.md`, `infrastructure.md`, `api-core.md`, `module-medical.md`, `module-birthdays.md`,
`auth-uiux-rework-stage.md`, `navigation-redesign-and-web-push.md`, `auth-email-anchor-jwt-rework.md`,
`settings-hub-and-account-security.md`. (`web-miniapp.md` помечен устаревшим — описывает старый
React-фронт, актуальная реализация — Angular, см. остальные файлы этого списка.)

### Architecture Decision Records — `docs/adr/`
`0001` локализация данных и egress, `0002` at-rest шифрование и управление ключами, `0003`
архитектура поиска (Postgres FTS, отказ от OpenSearch), `0004` исключение для Web Push, `0005`
исключение для обогащения справочника препаратов.

### Безопасность — `docs/security/`
`threat-model.md`, `access-matrix.md`, `backup-and-retention-policy.md`, и журнал модульного
аудита `module-review-2026-08-02/` (по одному файлу на модуль, находки приоритизированы
🔴/🟡/🟢, статус закрытия отслеживается в `00-INDEX.md`).

### Паттерны разработки
`.claude/patterns/backend.md`, `.claude/patterns/frontend_web.md` — устоявшиеся решения и грабли
(идентити-резолюшн, шифрованное поле + hash-колонка для lookup, Service Worker vs `ng serve`,
и т.д.), на которые стоит опираться при новом коде вместо переизобретения.

---

## 📊 Карта маршрутов API

| Группа | Маршруты |
|---|---|
| Auth | `POST /api/auth/{register/start,register/confirm,login,logout,logout-all,refresh,change-password,reset-password/start,reset-password/confirm,link-email/start,link-email/confirm,link-telegram/start}`, `GET /api/auth/{me,username-available,sessions}`, `POST /api/auth/sessions/{id}/revoke` |
| Auth · Telegram bind | `POST /api/auth/telegram/{init,send-code,bind,revoke}` |
| Families | `POST/GET /api/families`, `DELETE /api/families/{familyId}` |
| Invites | `POST /api/families/{familyId}/invites`, `GET /api/families/{familyId}/current`, `POST /api/invites/{code}/redeem`, `POST /api/invites/{inviteId}/revoke`, `GET /api/families/{familyId}/pending`, `POST /api/families/{familyId}/members/{targetUserId}/{approve,reject}` |
| Members | `POST /api/families/{familyId}/members/{targetUserId}/remove`, `POST /api/families/{familyId}/leave` |
| Consents | `GET /api/consents/{current,status}`, `POST /api/consents/accept`, `GET /api/legal/privacy-policy` |
| Account | `POST /api/account/delete`, `GET /api/account/export` |
| Medkits / Medications | `GET/POST /api/families/{familyId}/medkits`, `PUT/DELETE /api/medkits/{medkitId}`, `GET/POST /api/medkits/{medkitId}/medications`, `PUT/DELETE /api/medications/{medicationId}`, `POST /api/medications/ocr` |
| Справочник (KB) | `GET /api/kb/medications`, `GET /api/kb/medications/{id}`, `GET /api/medications/{medicationId}/kb`, `POST /api/medications/{medicationId}/kb/refresh` |
| Medical records (анализы + врачи, `?kind=analysis\|visit`) | `GET/POST /api/medical-records`, `GET /api/medical-records/shares`, `POST /api/medical-records/{share,unshare}`, `POST /api/medical-records/{recordId}/{hide,unhide}` |
| Attachments | `POST /api/medical-records/{recordId}/attachments`, `GET /api/medical-records/{recordId}/attachments`, `GET /api/attachments/{attachmentId}/url`, `GET /api/attachments/{attachmentId}/file` |
| Birthdays | `GET/POST /api/families/{familyId}/birthdays`, `PUT/DELETE /api/birthdays/{birthdayId}` |
| Search | `GET /api/search?types=` (`medication\|kb\|record\|visit\|birthday`) |
| Notifications | `GET /api/notifications?unreadOnly=`, `POST /api/notifications/{id}/read`, `GET/PUT /api/notifications/preferences` |
| Push | `GET /api/push/vapid-public-key`, `POST /api/push/{subscribe,unsubscribe}` |
| Bot | `POST /bot/webhook` (только если `Telegram:BotToken` задан) |

Все группы, кроме `Consents`/`Account` (частично анонимные для anti-enumeration) и `Auth`
(частично анонимные до входа), — `.RequireAuthorization()` по умолчанию; непокрытые маршруты
тоже требуют аутентификации через `FallbackPolicy`.

---

## 📱 Frontend — Angular PWA / Telegram Mini App

Один Angular 18 SPA обслуживает и браузер (PWA, service worker через `@angular/service-worker`),
и Telegram Mini App — поведение переключается по контексту (`isInsideTelegram()`), не отдельными
сборками. Собирается в статику, раздаётся `FamilyHub.Api`.

### Структура (`src/FamilyHub.Web/src/app/`)
- `components/` — вкладки навигации (`home`, `health-hub` с саб-роутами `/health/medications`,
  `/health/records`, `/health/visits`, `/health/kb`, `notifications-tab`, `settings`), формы
  (`login`, `telegram-bind`), сущностные панели (`families-tab`, `family-details`,
  `medications-panel`, `medkits-panel`, `medical-records-panel` — общая Panel для «Анализов» и
  «Врачей», параметризована `MedicalRecordKind`, обёрнута тонкими Page `medical-records-tab`/
  `doctor-visits-tab`, `birthdays-panel`/`-tab`, `birthday-widget`, `kb-card`/`kb-tab`),
  правовые модалки (`consent-gate`, `consent-text`, `privacy`), `dev-panel` (только dev-режим).
- `shared/` — переиспользуемые примитивы: `modal`, `bottom-sheet`, `confirm`, `toast`,
  `loading-spinner`, `search-field`, `cookie-banner`, общие утилиты (`util/`).
- `services/`, `models/` — HTTP-слой к API и TS-зеркало backend DTO.

### Навигация
Таб-бар: **Главная** (семьи + виджет ближайших ДР) / **Здоровье** (хаб: медикаменты + анализы +
врачи + справочник) / **Уведомления** / **Профиль**. Поиск — на Главной, серверные фильтры по типу
(`?types=medication,kb,record,visit,birthday`).

### Локальная разработка без Telegram
`http://localhost:<port>/?devTgId=<любое число>` — один раз кладёт `devTgId` в `localStorage`,
дальше работает как обычная сессия через `X-Dev-TelegramId`. Работает только при
`ASPNETCORE_ENVIRONMENT=Development`.

---

## 📦 Технологический стек

| Слой | Технология |
|---|---|
| Backend | ASP.NET Core Web API (.NET 8/9), модульный монолит |
| ORM / БД | EF Core / PostgreSQL |
| Frontend | Angular 18 + Bootstrap 5, PWA (service worker), тот же SPA — Telegram Mini App |
| Аутентификация | Email + пароль → JWT + DB-backed refresh-сессии (PWA); Telegram `initData` HMAC, lookup-only (Mini App) |
| Фоновые задачи | Hangfire на PostgreSQL (оповещения, обогащение справочника) |
| Объектное хранилище | MinIO (S3-совместимое) / локальный диск для dev |
| Логи | Serilog → Seq |
| Локальная LLM | LM Studio (OCR медикаментов по фото, суммаризация для справочника) |
| Веб-поиск для обогащения справочника | Yandex Web Search API / Brave (опционально, off по умолчанию) |
| Push-уведомления | Telegram-бот (Telegram.Bot) и/или Web Push (VAPID) |
| At-rest шифрование | AES-GCM, поле- и файл-уровень |

**Принцип:** ядро (API + модель доступа + данные) — одно, клиенты подключаются поверх общего
контракта. Бот — тонкий клиент, встроенный в тот же процесс, что и API.
