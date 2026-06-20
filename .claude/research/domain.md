# `FamilyHub.Domain`

Чистая модель данных — POCO-сущности и enum'ы, без зависимости от EF Core/ASP.NET. Любой
модуль может на неё ссылаться; сама она ни от чего не зависит.

## Сущности (`Entities/`)

| Сущность | Принадлежность | Назначение |
|---|---|---|
| `User` | — | Пользователь. `TelegramId` — основа авторизации. Может состоять в нескольких семьях (`Memberships`). |
| `Family` | — | Семья — владелец семейных ресурсов. `PlanType`/`PlanExpiresAt` — закладка под монетизацию (этап 5, не реализовано). |
| `FamilyMember` | many-to-many `User`↔`Family` | `Role` (`FamilyRole`) + `Status` (`MemberStatus`). UNIQUE(`FamilyId`,`UserId`) — членство в нескольких семьях уже заложено в модели. |
| `FamilyInvite` | семья | Одна таблица покрывает все сценарии инвайтов: одноразовый/многоразовый, персональный/по ссылке, с истечением. См. `RedeemResult` ниже. |
| `FamilyInviteRedemption` | — | Лог принятий инвайта. UNIQUE(`FamilyInviteId`,`UserId`) — повторный редимит запрещён на уровне БД. |
| `Medication` | `IFamilyOwned` (семья) | Аптечка. `ExpiryDate` — триггер для оповещений (`ReminderScanJob`). |
| `Birthday` | `IFamilyOwned` (семья) | День рождения члена семьи. |
| `MedicalRecord` | **пользователь**, не семья | Анализ. По умолчанию приватен. Видимость через `FamilyMedicalShare`/`MedicalRecordHidden`, не через роль в семье — поэтому интерфейс `IFamilyOwned` НЕ реализует. |
| `FamilyMedicalShare` | — | Уровень 1 шаринга анализов: «все мои анализы видны этой семье». UNIQUE(`OwnerUserId`,`FamilyId`). |
| `MedicalRecordHidden` | — | Уровень 2: точечно скрыть **конкретную** запись от **конкретной** уже расшаренной семьи. UNIQUE(`MedicalRecordId`,`FamilyId`). |
| `FileAttachment` | наследует от владельца (`OwnerType`+`OwnerId`) | Метаданные скана; сам файл — в объектном хранилище (`StorageKey`). Своей видимости нет — она наследуется от `MedicalRecord`/`Medication`, на который указывает `OwnerId`. |
| `Notification` | конкретный `UserId` | Доступ строго по получателю, не по роли в семье — даже владелец семьи не видит чужие оповещения. `DedupKey` (UNIQUE) — идемпотентность повторных прогонов джобы. |

## Enum'ы (`Enums/`)

- `FamilyRole` — `Member`/`Admin` (числовое сравнение `>=` используется в проверках доступа: `Admin >= Member`).
- `MemberStatus` — `Active`/`PendingApproval`.
- `RedeemResult` — все исходы `InviteService.RedeemInviteAsync`: `NotFound`, `Revoked`, `Expired`, `Exhausted`, `NotForYou`, `AlreadyMember`, `Joined` (персональный инвайт → сразу Active), `PendingApproval` (инвайт-ссылка → ждёт одобрения).
- `FileOwnerType` — к какой сущности относится `FileAttachment` (`MedicalRecord`/`Medication`).
- `NotificationType` — типы оповещений (`MedicationExpiringSoon`, `MedicationExpired`, `BirthdayUpcoming`).
- `PlanType` — закладка под монетизацию, пока всегда `Free`.

## `IFamilyOwned`

```csharp
public interface IFamilyOwned { Guid FamilyId { get; } }
```

Помечает сущность как «семейный ресурс». Используется `FamilyRoleHandler`
(`FamilyHub.Infrastructure.Authorization`) как generic-ресурс для resource-based авторизации.
**`MedicalRecord` сознательно его не реализует** — это центральное архитектурное решение,
разделяющее два класса ресурсов в системе (см. `infrastructure.md` → `FamilyAccessService`/
`FamilyRoleHandler` и `module-medical.md` → `MedicalRecordService`).

## На что обратить внимание при добавлении новой сущности

- Семейный ресурс (видим всем активным членам семьи по роли) → реализовать `IFamilyOwned`,
  завести `FamilyId`, использовать `IFamilyAccessService.HasRoleAsync` во всех CRUD-методах
  сервиса (по образцу `Medication`/`Birthday`).
- Персональный ресурс с собственной моделью шаринга (как `MedicalRecord`) → **не** реализовывать
  `IFamilyOwned`, видимость считать явным запросом-предикатом (см. `MedicalRecordService.VisibleRecordsQuery`).
- Любая новая M:N/уникальная связь — задавать `UNIQUE`-индекс в `Persistence/Configurations/*Configuration.cs`,
  не только проверкой в коде (см. `FamilyInviteRedemption`, `FamilyMedicalShare`, `Notification.DedupKey` —
  все потенциальные гонки в сервисах ловятся именно UNIQUE-индексом + `catch (DbUpdateException)`).
