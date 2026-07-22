# FamilyHub — Семейное приложение

## 📋 Суть проекта

**FamilyHub** — семейное приложение для хранения и шаринга медицинских данных, аптечки, дней рождения с разграничением доступа по семьям и ролям.

### Основные фичи
- **Аптечка** — лекарства, инструкции, сроки годности (с оповещениями)
- **Мед-анализы** — записи по датам, врачам, описанию + PDF-сканы. Персональные, с управляемым шарингом
- **Дни рождения** членов семьи
- **(Позже)** внутрисемейный чат и календарь событий

### Пользовательская модель
Пользователь может состоять в нескольких семьях одновременно с разными ролями. Разграничение доступа — центральная часть проекта.

---

## 🏗 Архитектура

### Модульный монолит

```
FamilyHub.sln
├── FamilyHub.Api               // эндпоинты, DI, auth, SignalR hub
├── FamilyHub.Domain            // сущности, enums, интерфейсы (IFamilyOwned)
├── FamilyHub.Infrastructure    // EF Core, MinIO-клиент, Hangfire, Telegram, Auth/Authorization
├── FamilyHub.Modules.Medical   // аптечка + анализы (ПЕРВЫЙ модуль)
├── FamilyHub.Modules.Birthdays // дни рождения
└── FamilyHub.TelegramBot       // Telegram.Bot, обработка апдейтов
```

**Принцип:** ядро (API + модель доступа + данные) делается один раз, клиенты подключаются по мере надобности. Бот — тонкий клиент поверх API.

### Зависимости модулей

Модульная структура с чёткими границами:
- `*.Modules.*` и `FamilyHub.Api` зависят от `FamilyHub.Domain` и `FamilyHub.Infrastructure`, но **никогда** друг от друга напрямую
- Общие сквозные сервисы (доступ по ролям, текущий пользователь, хранилище файлов, оповещения) живут в Infrastructure как абстракции

---

## 🎯 Модель доступа (ядро задачи)

### Два РАЗНЫХ типа владения

| Ресурс | Владелец | Кто видит | Кто управляет доступом |
|---|---|---|---|
| Аптечка, ДР, события, чат | **Семья** (`FamilyId`) | Все члены семьи (по роли) | Админ семьи |
| Мед-анализы | **Пользователь** (`OwnerUserId`) | Владелец + семьи, где расшарено и не скрыто | **Только владелец** |

### Роли в семье
Всего две роли: `Member` (просмотр + добавление/правка семейных ресурсов) и `Admin` (то же + приглашения и удаление участников). Понижения нет — только выгнать или выйти самому.

---

## 🗄 Схема БД

### Core-таблицы
```
User
  Id (PK), TelegramId, DisplayName, CreatedAt

Family
  Id (PK), Name, PlanType, PlanExpiresAt, CreatedAt

FamilyMember            -- many-to-many User<->Family + роль
  Id (PK), FamilyId (FK), UserId (FK), Role, Status, JoinedAt
  UNIQUE(FamilyId, UserId)

FamilyInvite            -- приглашение в семью (ссылка/код)
  Id (PK), FamilyId (FK), CreatedByUserId (FK), Code, TargetUserId, AssignedRole
  MaxUses, UsedCount, ExpiresAt, IsRevoked, CreatedAt

FamilyInviteRedemption  -- лог принятий
  Id (PK), FamilyInviteId (FK), UserId (FK), RedeemedAt
  UNIQUE(FamilyInviteId, UserId)
```

### Медицинский модуль
```
Medication              -- аптечка (семейный ресурс)
  Id (PK), FamilyId (FK), Name, Instructions, ExpiryDate, Quantity, CreatedByUserId

MedicalRecord           -- анализ (персональный ресурс)
  Id (PK), OwnerUserId (FK), PersonName, RecordDate, Doctor, Description, CreatedAt

FamilyMedicalShare      -- УРОВЕНЬ 1: владелец открыл свои анализы семье
  Id (PK), OwnerUserId (FK), FamilyId (FK), SharedAt
  UNIQUE(OwnerUserId, FamilyId)

MedicalRecordHidden     -- УРОВЕНЬ 2: точечное скрытие записи от семьи
  Id (PK), MedicalRecordId (FK), FamilyId (FK), HiddenAt
  UNIQUE(MedicalRecordId, FamilyId)

FileAttachment          -- метаданные сканов; файлы в MinIO
  Id (PK), OwnerType ('MedicalRecord'/'Medication'), OwnerId, StorageKey
  FileName, ContentType, SizeBytes, IsEncrypted, UploadedAt

Birthday                -- семейный ресурс
  Id (PK), FamilyId (FK), PersonName, Date
```

### Оповещения
```
Notification             -- оповещение о событиях
  Id (PK), UserId (FK), NotificationType, Title, Message, DedupKey
  SentAt, CreatedAt
  UNIQUE(DedupKey)
```

---

## 🔐 Логика видимости анализов

```csharp
// Видно, если: владелец, ИЛИ (мои анализы расшарены этой семье
// И я в ней состою И запись не скрыта именно от неё)
var visibleRecords = _db.MedicalRecords
    .Where(r =>
        r.OwnerUserId == userId                              // свои — всегда
        || _db.FamilyMedicalShares.Any(share =>
                share.OwnerUserId == r.OwnerUserId &&         // владелец открыл анализы
                _db.FamilyMembers.Any(m =>                    // активный член семьи
                    m.FamilyId == share.FamilyId &&
                    m.UserId == userId &&
                    m.Status == MemberStatus.Active) &&
                !_db.MedicalRecordsHidden.Any(h =>             // и запись не скрыта от неё
                    h.MedicalRecordId == r.Id &&
                    h.FamilyId == share.FamilyId)));
```

---

## 🔐 Проверка доступа (Authorization)

### Resource-based authorization в ASP.NET Core

```csharp
public enum FamilyRole { Member = 0, Admin = 1 }
public enum MemberStatus { PendingApproval = 0, Active = 1 }

public interface IFamilyOwned { Guid FamilyId { get; } }

public class FamilyRoleRequirement : IAuthorizationRequirement { ... }

public class FamilyRoleHandler
    : AuthorizationHandler<FamilyRoleRequirement, IFamilyOwned>
{
    private readonly AppDbContext _db;
    
    protected override async Task HandleRequirementAsync(...)
    {
        var userId = context.User.GetUserId();
        var membership = await _db.FamilyMembers.FirstOrDefaultAsync(m =>
            m.FamilyId == resource.FamilyId && m.UserId == userId);

        // PendingApproval не даёт доступа ни к чему, даже к семейным ресурсам
        if (membership is { Status: MemberStatus.Active }
            && membership.Role >= requirement.MinRole)
            context.Succeed(requirement);
    }
}
```

### Инварианты безопасности
1. **Никогда не загружать ресурс по `Id` без фильтра по семьям юзера.** Списки всегда фильтруются по членству.
2. **Шаринг и скрытие анализов — только владелец** (`OwnerUserId == userId`), даже админ семьи не может.
3. **Семейные ресурсы — через роль** в той семье, которой принадлежит ресурс.
4. Форма «скрыть при создании» показывает только семьи, которым у владельца уже есть `FamilyMedicalShare`.
5. При отключении шаринга семье строки `MedicalRecordHidden` можно НЕ чистить — при повторном включении деликатное останется скрытым.

---

## 📬 Приглашения и удаление участников

### Гибридное одобрение (ключевое)
- **Персональный инвайт** (`TargetUserId` задан) → вступление **сразу `Active`**, без одобрения
- **Ссылка-инвайт** (`TargetUserId = null`) → вступление в статусе **`PendingApproval`**, пока админ не подтвердит

Пока статус `PendingApproval`, человек **не видит вообще ничего** в семье — даже аптечку и ДР. Членство существует, но неактивно. Все проверки доступа требуют `Status == Active`.

### Защита для медданных
Даже после активации вступление НЕ открывает чужие анализы. Активный член видит только семейные ресурсы (аптечка, ДР). Анализы остаются приватными у каждого до явного шаринга. Два независимых барьера: статус членства и владельческий шаринг.

### Инварианты
1. Создавать инвайт может только **Admin** семьи.
2. Персональный инвайт → сразу `Active`; ссылка-инвайт → `PendingApproval` до одобрения админа.
3. `PendingApproval` не даёт доступа ни к чему — даже к семейным ресурсам.
4. Одобрять/отклонять заявки может только **Admin** семьи.
5. Вступление по инвайту НЕ открывает чужие анализы.
6. Инкремент `UsedCount` + вступление — в одной транзакции (гонка на `MaxUses`).
7. Персональный инвайт принимает только адресат.
8. Выгнать может только Admin; выйти сам может любой; **последнего админа убрать нельзя**.
9. При выходе/удалении — автоматически чистится `FamilyMedicalShare` ушедшего для этой семьи.

---

## 🖥 Инфраструктура (тонкий ВДС + домашний ПК)

### Распределение
- **На ВДС:** приложение + PostgreSQL + Redis. БД и realtime НЕ выносить на домашний ПК — разрыв VPN/перезагрузка роутера положит сервис.
- **На домашнем ПК:** только MinIO (объектное хранилище PDF-сканов). Файлы терпят задержку и недоступность лучше, чем БД.

### Файлы (сканы)
- В БД только метаданные (`FileAttachment`), сами файлы в MinIO.
- Доступ к файлу — через короткоживущие **pre-signed URL** (минуты), после проверки доступа через policy. Никаких прямых постоянных ссылок на MinIO.

---

## 🔐 Безопасность Telegram Mini App

1. **Валидация `initData`** — обязательно на бэкенде через HMAC с токеном бота. Без этого любой подделает Telegram ID и зайдёт в чужую семью. Делается ПЕРВЫМ, до бизнес-логики.
2. **Контекст текущей семьи** — раз юзер в нескольких семьях, почти каждый запрос к семейным ресурсам несёт `familyId`, и членство проверяется.
3. **PDF-сканы** — только через pre-signed URL с проверкой доступа.

---

## ⚖️ Правовая заметка

Хранятся чужие медицинские данные. При монетизации и пользователях извне семьи это попадает под закон о персональных данных (РФ — 152-ФЗ, медданные — спецкатегория с повышенными требованиями). Заложить возможность **шифрования сканов** в хранилище (поле `IsEncrypted` уже есть), чтобы включить без переделки архитектуры.

---

## 📅 План реализации (поэтапно)

### Этап 1 — Ядро доступа + первый модуль
1. Solution-структура (модульный монолит).
2. Сущности: `User`, `Family`, `FamilyMember`, EF Core + миграции.
3. Telegram-аутентификация + валидация Mini App initData.
4. Authorization handler (`FamilyRoleHandler`, `IFamilyOwned`).
5. Приглашения и одобрение, выгон/самовыход (зачистка `FamilyMedicalShare`, защита последнего админа).
6. Модуль **Аптечка** (`Medication`) — CRUD со сроками, проверка доступа по роли.

### Этап 2 — Анализы и файлы
7. `MedicalRecord` + `FamilyMedicalShare` + `MedicalRecordHidden`.
8. Эндпоинты: загрузить анализ, расшарить анализы семье, скрыть запись от семей, получить видимые записи.
9. MinIO-интеграция: загрузка скана + pre-signed URL.

### Этап 3 — Оповещения
10. Hangfire/Quartz: оповещения о сроках годности лекарств.

### Этап 4 — Дни рождения + бот как клиент
11. Модуль `Birthday`.
12. Telegram-бот как тонкий клиент + Mini App для PDF и таблиц.

### Этап 5 — Монетизация
13. Тарифы на уровне `Family` (`PlanType`, лимиты: число семей, объём сканов, число членов).

### Этап 6+ — Расширения
14. Внутрисемейный чат (SignalR + Redis).
15. Календарь событий с управляемыми оповещениями.

---

## 🚀 Первый практический шаг для Claude Code

Собрать каркас Этапа 1–2 Medical-модуля:
- entity-классы (`MedicalRecord`, `FamilyMedicalShare`, `MedicalRecordHidden`, `Medication`, `FamilyMember` с двумя ролями, `FamilyInvite`, `FamilyInviteRedemption`);
- `AppDbContext` с конфигурацией, UNIQUE-ограничениями и индексами;
- начальную миграцию;
- эндпоинты: создать/принять/отозвать инвайт, одобрить/отклонить заявку (для ссылок), выгнать участника, выйти из семьи; загрузить анализ, расшарить семье, скрыть от семей, получить видимые записи, + CRUD аптечки;
- `FamilyRoleHandler` (с проверкой `Status == Active`), проверку владельца для операций над анализами, защиту последнего админа и зачистку шаринга при выходе.

---

## 📁 Исследование архитектуры

Файлы ресёрча в `.claude/research/`:
- [`domain.md`](./research/domain.md) — `FamilyHub.Domain`: сущности, enum'ы, инварианты владения ресурсами.
- [`infrastructure.md`](./research/infrastructure.md) — `FamilyHub.Infrastructure`: аутентификация, авторизация, БД/EF Core, файловое хранилище, оповещения, Telegram-интеграция.
- [`api-core.md`](./research/api-core.md) — `FamilyHub.Api`: семьи/инвайты/участники/оповещения, бот-вебхук, `Program.cs`.
- [`module-medical.md`](./research/module-medical.md) — `FamilyHub.Modules.Medical`: аптечка, анализы (персональный ресурс с шарингом), вложения.
- [`module-birthdays.md`](./research/module-birthdays.md) — `FamilyHub.Modules.Birthdays`: дни рождения.
- [`web-miniapp.md`](./research/web-miniapp.md) — `FamilyHub.Web`: React Mini App (Telegram-клиент), её контракт с API.

**Архитектура одной картинкой:** модульный монолит с зависимостями только в одну сторону: `*.Modules.*` и `FamilyHub.Api` зависят от `FamilyHub.Domain` и `FamilyHub.Infrastructure`, но **никогда** друг от друга напрямую. Общие сквозные сервисы (доступ по ролям, текущий пользователь, хранилище файлов, отправка оповещений) живут в Infrastructure как абстракции и подключаются модулям через DI.

---

## 📊 Точная карта маршрутов API

| Группа | Маршруты |
|---|---|
| Families | `POST /api/families`, `GET /api/families` |
| Invites | `POST /api/families/{familyId}/invites`, `POST /api/invites/{code}/redeem`, `POST /api/invites/{inviteId}/revoke`, `GET /api/families/{familyId}/pending`, `POST /api/families/{familyId}/members/{targetUserId}/approve`, `POST /api/families/{familyId}/members/{targetUserId}/reject` |
| Members | `POST /api/families/{familyId}/members/{targetUserId}/remove`, `POST /api/families/{familyId}/leave` |
| Notifications | `GET /api/notifications?unreadOnly=`, `POST /api/notifications/{id}/read` |
| Medications | `GET/POST /api/families/{familyId}/medications`, `PUT/DELETE /api/medications/{medicationId}` |
| Birthdays | `GET/POST /api/families/{familyId}/birthdays`, `PUT/DELETE /api/birthdays/{birthdayId}` |
| Medical records | `GET/POST /api/medical-records`, `POST /api/medical-records/share`, `POST /api/medical-records/unshare`, `POST /api/medical-records/{recordId}/hide`, `POST /api/medical-records/{recordId}/unhide` |
| Attachments | `POST /api/medical-records/{recordId}/attachments`, `GET /api/attachments/{attachmentId}/url` |
| Bot | `POST /bot/webhook` (только если `Telegram:BotToken` задан) |

Все группы — `.RequireAuthorization()` по умолчанию.

---

## 📱 Telegram Mini App

React + TypeScript + Vite SPA, собирается прямо в `FamilyHub.Api/wwwroot`. Тот же origin, что и API → CORS не нужен.

### Структура
- `src/telegram.ts` — обёртка над `window.Telegram.WebApp`: `initTelegram()`, `getInitData()`, `isInsideTelegram()`, `openExternalLink()`
- `src/api.ts` — единственная точка HTTP-вызовов к API. `authHeaders()`: внутри Telegram → `Authorization: tma <initData>`, снаружи → `X-Dev-TelegramId` из query-параметра.
- `src/types.ts` — TS-зеркало backend DTO. Enum'ы объявлены как `const`-объекты с `as const`.
- `src/main.tsx` — точка входа, вызывает `initTelegram()` перед рендером.
- `src/App.tsx` — вкладочная навигация (`'families' | 'medications' | 'birthdays' | 'records' | 'notifications'`).

### Локальная разработка без Telegram
`http://localhost:<port>/?devTgId=<любое число>` — один раз кладёт `devTgId` в `localStorage`, дальше работает как обычная авторизованная сессия через `X-Dev-TelegramId`. Работает только если API запущен с `ASPNETCORE_ENVIRONMENT=Development`.

---

## 📦 Технологический стек

| Слой | Технология |
|---|---|
| Backend | ASP.NET Core Web API (.NET 8/9) |
| ORM | EF Core |
| БД | PostgreSQL |
| Realtime (для будущего чата) | SignalR + Redis backplane |
| Фоновые задачи (оповещения) | Hangfire или Quartz.NET |
| Объектное хранилище (сканы PDF) | MinIO (S3-совместимое) |
| Авторизация | Telegram ID + валидация Mini App initData (HMAC) |
| Первый клиент | Telegram-бот (Telegram.Bot) + Telegram Mini App |

**Принцип:** ядро (API + модель доступа + данные) делается один раз, клиенты подключаются по мере надобности. Бот — тонкий клиент поверх API.

---

## 📝 Стартовый контекст

> Семейное приложение для хранения медицинских данных, аптечки, дней рождения, с разграничением доступа по семьям и ролям. Старт — Telegram-бот + Mini App. Закладывается под продукт и монетизацию.