# FamilyHub — Project Brief

> Стартовый контекст для разработки. Семейное приложение для хранения медицинских данных, аптечки, дней рождения, с разграничением доступа по семьям и ролям. Старт — Telegram-бот + Mini App. Закладывается под продукт и монетизацию.

---

## 1. Суть проекта

Приложение для семейного хранения и шаринга:
- **Аптечка** — лекарства, инструкции, сроки годности (с оповещениями).
- **Мед-анализы** — записи по датам, врачам, описанию + PDF-сканы. Персональные, с управляемым шарингом.
- **Дни рождения** членов семьи.
- **(Позже)** внутрисемейный чат и календарь событий с управляемыми оповещениями.

Пользователь может состоять **в нескольких семьях** одновременно с разными ролями. Разграничение доступа — центральная часть проекта.

---

## 2. Технологический стек

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

## 3. Архитектура — модульный монолит

Один ASP.NET Core процесс, но с чёткими границами модулей. Не микросервисы на старте (оверинжиниринг). Модули зависят только от `Domain` и `Infrastructure`, не друг от друга — чтобы при росте нагрузки модуль можно было вынести в отдельный сервис.

```
FamilyHub.sln
├── FamilyHub.Api               // эндпоинты, DI, auth, SignalR hub
├── FamilyHub.Domain            // сущности, enums, интерфейсы (IFamilyOwned)
├── FamilyHub.Infrastructure    // EF Core, MinIO-клиент, Hangfire
├── FamilyHub.Modules.Medical   // аптечка + анализы (ПЕРВЫЙ модуль)
├── FamilyHub.Modules.Chat      // позже
├── FamilyHub.Modules.Calendar  // позже
└── FamilyHub.TelegramBot       // Telegram.Bot, обработка апдейтов
```

---

## 4. Модель доступа (ядро задачи)

Два РАЗНЫХ типа владения. Это фундаментально, не упрощать.

### 4.1 Семейные ресурсы (аптечка, ДР, события, будущий чат)
- Принадлежат **семье** (`FamilyId` на ресурсе).
- Видны всем членам семьи по роли.
- Управляет админ семьи.

### 4.2 Мед-анализы — персональное владение + пер-семейный шаринг
- Принадлежат **пользователю** (`OwnerUserId`), НЕ семье.
- **По умолчанию приватны** — видны только владельцу (opt-in, безопасный дефолт).
- Владелец в настройках видимости открывает свои анализы **выбранным семьям** (уровень 1 — на уровне всех своих анализов, одним действием).
- При создании записи можно точечно **скрыть** её от выбранных семей из числа тех, кому уже открыт доступ (уровень 2 — исключение).
- Управляет шарингом и скрытием **ТОЛЬКО владелец.** Админ семьи сюда не лезет.

### Таблица владения

| Ресурс | Владелец | Кто видит | Кто управляет доступом |
|---|---|---|---|
| Аптечка | Семья | Все члены (по роли) | Админ семьи |
| ДР, события | Семья | Все члены | Админ семьи |
| Мед-анализ | **Пользователь** | Владелец + семьи, где расшарено и не скрыто | **Только владелец** |
| Будущий чат | Семья | Члены семьи | Админ семьи |

### Роли в семье
Всего две роли: `Member` (просмотр + добавление/правка семейных ресурсов) и `Admin` (то же + приглашения и удаление участников). Понижения нет — только выгнать или выйти самому. Модель «админ семьи = админ группы в Telegram».

---

## 5. Схема БД

```
User
  Id (PK)
  TelegramId            -- авторизация через Telegram
  DisplayName
  CreatedAt

Family
  Id (PK)
  Name
  PlanType              -- закладка под монетизацию (Free/Paid)
  PlanExpiresAt         -- закладка под монетизацию
  CreatedAt

FamilyMember            -- many-to-many User<->Family + роль
  Id (PK)
  FamilyId (FK)
  UserId (FK)
  Role                  -- Admin / Member (enum, всего две роли)
  Status                -- Active / PendingApproval (enum)
  JoinedAt
  UNIQUE(FamilyId, UserId)   -- членство в нескольких семьях уже заложено

FamilyInvite            -- приглашение в семью (ссылка/код)
  Id (PK)
  FamilyId (FK)
  CreatedByUserId (FK)  -- должен быть Admin
  Code                  -- случайный токен (UNIQUE, индекс)
  TargetUserId (FK, null)  -- задан → персональный инвайт
  AssignedRole          -- роль при вступлении (по умолчанию Member)
  MaxUses               -- лимит (1 = одноразовый)
  UsedCount
  ExpiresAt             -- срок жизни (null = бессрочно)
  IsRevoked
  CreatedAt

FamilyInviteRedemption  -- лог принятий
  Id (PK)
  FamilyInviteId (FK)
  UserId (FK)
  RedeemedAt
  UNIQUE(FamilyInviteId, UserId)

-- === МЕДИЦИНСКИЙ МОДУЛЬ ===

Medication              -- аптечка (семейный ресурс)
  Id (PK)
  FamilyId (FK)         -- всегда через семью
  Name
  Instructions
  ExpiryDate            -- для оповещений
  Quantity
  CreatedByUserId (FK)
  CreatedAt

MedicalRecord           -- анализ (персональный ресурс)
  Id (PK)
  OwnerUserId (FK)      -- ВЛАДЕЛЕЦ, не семья
  PersonName
  RecordDate
  Doctor
  Description
  CreatedAt

FamilyMedicalShare      -- УРОВЕНЬ 1: владелец открыл свои анализы семье
  Id (PK)
  OwnerUserId (FK)      -- чьи анализы
  FamilyId (FK)         -- какой семье открыты
  SharedAt
  UNIQUE(OwnerUserId, FamilyId)

MedicalRecordHidden     -- УРОВЕНЬ 2: точечное скрытие записи от семьи
  Id (PK)
  MedicalRecordId (FK)
  FamilyId (FK)
  HiddenAt
  UNIQUE(MedicalRecordId, FamilyId)

FileAttachment          -- метаданные сканов; файлы в MinIO
  Id (PK)
  OwnerType             -- 'MedicalRecord' / 'Medication'
  OwnerId
  StorageKey            -- ключ в объектном хранилище
  FileName
  ContentType
  SizeBytes
  IsEncrypted           -- закладка под шифрование медданных
  UploadedAt
  -- доступ наследуется от родительской записи, своей видимости нет

Birthday                -- семейный ресурс
  Id (PK)
  FamilyId (FK)
  PersonName
  Date
```

---

## 6. Логика видимости анализов (главный запрос)

```csharp
// Видно, если: владелец, ИЛИ (мои анализы расшарены этой семье
// И я в ней состою И запись не скрыта именно от неё)
var visibleRecords = _db.MedicalRecords
    .Where(r =>
        r.OwnerUserId == userId                              // свои — всегда
        || _db.FamilyMedicalShares.Any(share =>
               share.OwnerUserId == r.OwnerUserId &&         // владелец открыл анализы
               _db.FamilyMembers.Any(m =>
                   m.FamilyId == share.FamilyId &&
                   m.UserId == userId &&
                   m.Status == MemberStatus.Active) &&           // активный член семьи
               !_db.MedicalRecordsHidden.Any(h =>
                   h.MedicalRecordId == r.Id &&
                   h.FamilyId == share.FamilyId)));           // и запись не скрыта от неё
```

---

## 7. Проверка доступа (Authorization)

Resource-based authorization в ASP.NET Core. Один handler проверяет членство + роль.

```csharp
public enum FamilyRole { Member = 0, Admin = 1 }

public enum MemberStatus { PendingApproval = 0, Active = 1 }

public interface IFamilyOwned { Guid FamilyId { get; } }

public class FamilyRoleRequirement : IAuthorizationRequirement
{
    public FamilyRole MinRole { get; }
    public FamilyRoleRequirement(FamilyRole minRole) => MinRole = minRole;
}

public class FamilyRoleHandler
    : AuthorizationHandler<FamilyRoleRequirement, IFamilyOwned>
{
    private readonly AppDbContext _db;
    public FamilyRoleHandler(AppDbContext db) => _db = db;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FamilyRoleRequirement requirement,
        IFamilyOwned resource)
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

### Инварианты безопасности (не нарушать)
1. **Никогда не загружать ресурс по `Id` без фильтра по семьям юзера.** Списки всегда фильтруются по членству.
2. **Шаринг и скрытие анализов — только владелец** (`OwnerUserId == userId`), даже админ семьи не может.
3. **Семейные ресурсы — через роль** в той семье, которой принадлежит ресурс.
4. Форма «скрыть при создании» показывает только семьи, которым у владельца уже есть `FamilyMedicalShare` (пересечение «мои семьи» ∩ «расшаренные семьи»).
5. При отключении шаринга семье строки `MedicalRecordHidden` можно НЕ чистить — при повторном включении деликатное останется скрытым (желательное поведение).

---

## 8. Приглашения и удаление участников

### Приглашение — код/ссылка-инвайт с ограничениями
Одна таблица покрывает все сценарии: одноразовая ссылка, многоразовая, персональная, с истечением. Канал — Telegram (ссылка в личку → Mini App с экраном «Вступить в семью X» → подтверждение).

**Гибридное одобрение (ключевое):**
- **Персональный инвайт** (`TargetUserId` задан) → вступление **сразу `Active`**, без одобрения. Инвайт адресный, примет только указанный человек, доверие уже есть.
- **Ссылка-инвайт** (`TargetUserId = null`) → вступление в статусе **`PendingApproval`**, пока админ не подтвердит. Канал менее контролируемый (ссылка может утечь), поэтому админ решает, пускать ли.

Пока статус `PendingApproval`, человек **не видит вообще ничего** в семье — даже аптечку и ДР. Членство существует, но неактивно. Все проверки доступа требуют `Status == Active`.

**Защита для медданных (отдельный слой):** даже после активации вступление НЕ открывает чужие анализы. Активный член видит только семейные ресурсы (аптечка, ДР). Анализы остаются приватными у каждого до явного шаринга владельцем. Два независимых барьера: статус членства (для семейных ресурсов) и владельческий шаринг (для анализов).

```
FamilyInvite
  Id (PK)
  FamilyId (FK)            -- куда приглашают
  CreatedByUserId (FK)     -- кто создал (должен быть Admin)
  Code                     -- случайный токен для ссылки/кода (UNIQUE, индекс)
  TargetUserId (FK, null)  -- если задан → персональное, примет только он
  AssignedRole             -- роль при вступлении (по умолчанию Member)
  MaxUses                  -- лимит использований (1 = одноразовая)
  UsedCount                -- сколько раз уже использовано
  ExpiresAt                -- срок жизни (null = бессрочно, не рекомендуется)
  IsRevoked                -- админ может отозвать вручную
  CreatedAt

FamilyInviteRedemption     -- лог, кто по какому инвайту вступил
  Id (PK)
  FamilyInviteId (FK)
  UserId (FK)
  RedeemedAt
  UNIQUE(FamilyInviteId, UserId)
```

Комбинации:
- **Одноразовая ссылка** → `TargetUserId = null`, `MaxUses = 1`.
- **Многоразовая** (позвать сразу нескольких) → `MaxUses = N`.
- **Персональная** → `TargetUserId = конкретный юзер`, примет только он.
- **С истечением** → `ExpiresAt`.

### Логика принятия инвайта

```csharp
public async Task<RedeemResult> RedeemInviteAsync(string code, Guid userId)
{
    var invite = await _db.FamilyInvites.FirstOrDefaultAsync(i => i.Code == code);

    if (invite is null)                       return RedeemResult.NotFound;
    if (invite.IsRevoked)                     return RedeemResult.Revoked;
    if (invite.ExpiresAt is { } exp && exp < DateTime.UtcNow)
                                              return RedeemResult.Expired;
    if (invite.UsedCount >= invite.MaxUses)   return RedeemResult.Exhausted;
    if (invite.TargetUserId is { } target && target != userId)
                                              return RedeemResult.NotForYou;

    var already = await _db.FamilyMembers
        .AnyAsync(m => m.FamilyId == invite.FamilyId && m.UserId == userId);
    if (already) return RedeemResult.AlreadyMember;

    // Гибрид: персональный инвайт → сразу Active; ссылка → PendingApproval
    var status = invite.TargetUserId is not null
        ? MemberStatus.Active
        : MemberStatus.PendingApproval;

    // вступление + инкремент в ОДНОЙ транзакции (защита от гонки на MaxUses)
    using var tx = await _db.Database.BeginTransactionAsync();
    _db.FamilyMembers.Add(new FamilyMember {
        FamilyId = invite.FamilyId, UserId = userId,
        Role = invite.AssignedRole, Status = status, JoinedAt = DateTime.UtcNow });
    _db.FamilyInviteRedemptions.Add(new FamilyInviteRedemption {
        FamilyInviteId = invite.Id, UserId = userId, RedeemedAt = DateTime.UtcNow });
    invite.UsedCount++;
    await _db.SaveChangesAsync();
    await tx.CommitAsync();

    return status == MemberStatus.Active
        ? RedeemResult.Joined           // персональный — вступил сразу
        : RedeemResult.PendingApproval; // ссылка — ждёт одобрения админа
}

// Админ подтверждает заявку (только Admin семьи)
public async Task ApproveMemberAsync(Guid familyId, Guid targetUserId)
{
    var member = await _db.FamilyMembers.FirstOrDefaultAsync(m =>
        m.FamilyId == familyId && m.UserId == targetUserId
        && m.Status == MemberStatus.PendingApproval);
    if (member is null) return;

    member.Status = MemberStatus.Active;
    await _db.SaveChangesAsync();
}

// Админ отклоняет заявку → membership удаляется
public async Task RejectMemberAsync(Guid familyId, Guid targetUserId)
{
    var member = await _db.FamilyMembers.FirstOrDefaultAsync(m =>
        m.FamilyId == familyId && m.UserId == targetUserId
        && m.Status == MemberStatus.PendingApproval);
    if (member is null) return;

    _db.FamilyMembers.Remove(member);
    await _db.SaveChangesAsync();
}
```

### Поток одобрения (для ссылок-инвайтов)
1. Человек переходит по ссылке → `RedeemInviteAsync` создаёт `FamilyMember` со `Status = PendingApproval` → возвращает `PendingApproval`. UI показывает «Заявка отправлена, ждём подтверждения админа».
2. Админ видит список заявок семьи (`Status == PendingApproval`) → жмёт «Принять» (`ApproveMemberAsync` → `Active`) или «Отклонить» (`RejectMemberAsync` → удаление членства).
3. Пока `PendingApproval` — человек не видит ни аптечку, ни ДР, ни тем более анализы. Все проверки требуют `Active`.

**Нюанс с `MaxUses`:** для ссылки `UsedCount` инкрементится в момент перехода, ещё до одобрения. Если админ отклонил — слот «потрачен». Для одноразовой ссылки (`MaxUses = 1`) это значит: один отклонённый → ссылка исчерпана. Если это нежелательно, при `Reject` можно декрементить `UsedCount` обратно (по желанию — заложи флагом, в каркасе по умолчанию НЕ декрементим, проще и предсказуемее).


Две роли, понижения нет — только выгнать или выйти самому.
- **Выгнать** любого участника может **Admin** семьи.
- **Выйти самому** может любой участник (роль не требуется).
- При выходе/удалении **автоматически чистится `FamilyMedicalShare`** ушедшего для этой семьи: вышел → его анализы перестают быть видны этой семье. Сами записи и сканы остаются у владельца.
- **Нельзя удалить/вывести последнего админа** — семья не должна остаться без управления. Перед операцией: если это последний `Admin`, блокировать, пока не назначен другой.

```csharp
public async Task RemoveMemberAsync(Guid familyId, Guid targetUserId)
{
    var member = await _db.FamilyMembers
        .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.UserId == targetUserId);
    if (member is null) return;

    if (member.Role == FamilyRole.Admin)
    {
        var adminCount = await _db.FamilyMembers
            .CountAsync(m => m.FamilyId == familyId
                && m.Role == FamilyRole.Admin
                && m.Status == MemberStatus.Active);
        if (adminCount <= 1) throw new InvalidOperationException("Last admin");
    }

    var shares = _db.FamilyMedicalShares
        .Where(s => s.FamilyId == familyId && s.OwnerUserId == targetUserId);
    _db.FamilyMedicalShares.RemoveRange(shares);

    _db.FamilyMembers.Remove(member);
    await _db.SaveChangesAsync();
}
```

### Инварианты приглашений/удаления (не нарушать)
1. Создавать инвайт может только **Admin** семьи.
2. Персональный инвайт (`TargetUserId`) → вступление сразу `Active`; ссылка-инвайт → `PendingApproval` до одобрения админа.
3. **`PendingApproval` не даёт доступа ни к чему** — даже к семейным ресурсам. Все проверки доступа требуют `Status == Active`.
4. Одобрять/отклонять заявки может только **Admin** семьи.
5. Вступление по инвайту НЕ открывает чужие анализы — это отдельный барьер (владельческий шаринг), не связанный со статусом членства.
6. Инкремент `UsedCount` + вступление — в одной транзакции (гонка на `MaxUses`).
7. Персональный инвайт (`TargetUserId`) принимает только адресат.
8. Выгнать может только Admin; выйти сам может любой; **последнего админа убрать нельзя** (считаются только `Active`-админы).
9. При выходе/удалении — автоматически чистится `FamilyMedicalShare` ушедшего для этой семьи.

---

## 9. Инфраструктура (тонкий ВДС + домашний ПК)

Схема: тонкий ВДС с проксированием на домашний ПК через VPN.

**Распределение (важно):**
- **На ВДС:** приложение + PostgreSQL + Redis. БД и realtime НЕ выносить на домашний ПК — разрыв VPN/перезагрузка роутера положит сервис.
- **На домашнем ПК:** только MinIO (объектное хранилище PDF-сканов). Файлы терпят задержку и недоступность лучше, чем БД: если дом отвалился — временно не грузятся сканы, но чат/календарь/аптечка работают.

**Файлы (сканы):**
- В БД только метаданные (`FileAttachment`), сами файлы в MinIO.
- Доступ к файлу — через короткоживущие **pre-signed URL** (минуты), после проверки доступа через policy. Никаких прямых постоянных ссылок на MinIO.

---

## 10. Безопасность Telegram Mini App

1. **Валидация `initData`** — обязательно на бэкенде через HMAC с токеном бота. Без этого любой подделает Telegram ID и зайдёт в чужую семью. Делается ПЕРВЫМ, до бизнес-логики. Алгоритм — в офиц. документации Telegram по Mini Apps.
2. **Контекст текущей семьи** — раз юзер в нескольких семьях, почти каждый запрос к семейным ресурсам несёт `familyId`, и членство проверяется.
3. **PDF-сканы** — только через pre-signed URL с проверкой доступа.

---

## 11. Правовая заметка

Хранятся чужие медицинские данные. При монетизации и пользователях извне семьи это попадает под закон о персональных данных (РФ — 152-ФЗ, медданные — спецкатегория с повышенными требованиями). Заложить возможность **шифрования сканов** в хранилище (поле `IsEncrypted` уже есть), чтобы включить без переделки архитектуры.

---

## 12. План реализации (поэтапно)

### Этап 1 — Ядро доступа + первый модуль
1. Solution-структура (модульный монолит).
2. Сущности: `User`, `Family`, `FamilyMember`, EF Core + миграции.
3. Telegram-аутентификация + валидация Mini App initData.
4. Authorization handler (`FamilyRoleHandler`, `IFamilyOwned`).
5. Приглашения и одобрение: `FamilyInvite`, `FamilyInviteRedemption`, статус `Active/PendingApproval`. Персональный инвайт → сразу Active; ссылка → PendingApproval + эндпоинты одобрения/отклонения админом. Выгон и самовыход (зачистка `FamilyMedicalShare`, защита последнего админа).
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

## 13. Первый практический шаг для Claude Code

Собрать каркас Этапа 1–2 Medical-модуля:
- entity-классы (`MedicalRecord`, `FamilyMedicalShare`, `MedicalRecordHidden`, `Medication`, `FamilyMember` с двумя ролями, `FamilyInvite`, `FamilyInviteRedemption`);
- `AppDbContext` с конфигурацией, UNIQUE-ограничениями и индексами;
- начальную миграцию;
- эндпоинты: создать/принять/отозвать инвайт, одобрить/отклонить заявку (для ссылок), выгнать участника, выйти из семьи; загрузить анализ, расшарить семье, скрыть от семей, получить видимые записи, + CRUD аптечки;
- `FamilyRoleHandler` (с проверкой `Status == Active`), проверку владельца для операций над анализами, защиту последнего админа и зачистку шаринга при выходе.
