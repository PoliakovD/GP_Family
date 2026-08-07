# Модуль: Семьи, инвайты, членство, аккаунт, согласия

**Файлы:** `Api/Features/Families/*`, `Api/Features/Invites/*`, `Api/Features/Members/*`,
`Api/Features/Account/*`, `Api/Features/Consents/*`, `Infrastructure/Consents/*`

**Статус:** 🔴 1/1 закрыта, 🟡 2/3 закрыты (находки 1, 2, 4 — см. пометки ✅; №3 —
производительность, не безопасность, осталась открытой).

## Сводка

Основная бизнес-логика семей/инвайтов/членства сделана последовательно: везде, кроме одного места,
доступ проверяется через `IFamilyAccessService.HasRoleAsync` до чтения/изменения данных, есть
защита от гонок (транзакция на редемпшн инвайта, guard на «последнего админа»). Но найден один
явный broken-access-control баг — эндпоинт, выпадающий из общего паттерна.

## 🔴 Высокий приоритет

### 1. `GET /api/families/{familyId}/current` — отдаёт список участников ЛЮБОЙ семьи без проверки членства

> ✅ **Исправлено.** `FamilyService.GetFamilyMembersAsync` теперь принимает `requestingUserId` и
> проверяет `access.HasRoleAsync(..., FamilyRole.Member, ...)` до чтения, эндпоинт возвращает
> `403` при отказе — тем же паттерном, что и остальные сервисы модуля. Фронтенд
> (`family-state.service.ts`) обновлён, чтобы не валить весь `refresh()` при `403` по одной
> семье (например, для `PendingApproval`-заявок). Покрыто тремя регресс-тестами в
> `FamiliesAndInvitesFlowTests.cs` (посторонний → 403, активный участник → 200, ожидающий
> подтверждения заявитель → 403).

- **Где:** `Api/Features/Invites/InviteEndpoints.cs:33-39` →
  `Api/Features/Families/FamilyService.cs:63-73` (`GetFamilyMembersAsync`).
  ```csharp
  group.MapGet("/families/{familyId:guid}/current",
      async (Guid familyId, FamilyService service, CancellationToken ct) =>
      {
          var result = await service.GetFamilyMembersAsync(familyId, ct);
          return Results.Ok(result);
      }
  );
  ```
  `GetFamilyMembersAsync(familyId, ct)` вообще не принимает `userId` и не делает никакой проверки
  доступа — просто выбирает всех `FamilyMembers` по `FamilyId`.
- **Почему это важно:** эндпоинт находится под общей `RequireAuthorization()` (группа `/api`), то
  есть требует ТОЛЬКО валидную аутентификацию — не членство в конкретной семье. Любой
  зарегистрированный пользователь FamilyHub, зная (или подобрав/увидев где-то раньше) GUID чужой
  семьи, получает `DisplayName`, `Username`, `Role`, `JoinedAt` всех её участников. Это прямое
  нарушение инварианта «доступ к семейным ресурсам — по роли в этой семье», который явно
  выдерживается во ВСЕХ соседних сервисах (`MedkitService`, `MedicationService`, `BirthdayService`,
  `InviteService.GetPendingMembersAsync`, `FamilyAccessService` и т.д. — везде первым шагом идёт
  `access.HasRoleAsync(userId, familyId, FamilyRole.Member, ct)`).
  `FamilyId` — v4 GUID (128 бит), так что слепой перебор непрактичен, но это не снимает проблему:
  GUID семьи регулярно попадает в клиентские данные того, кто когда-либо состоял в ней (в т.ч.
  бывшие участники после выхода/выгона — `FamilyId` у них никуда не девается из истории), в deep
  link'и инвайтов, в контекст поисковой выдачи и т.д. Любой из этих людей навсегда сохраняет
  возможность читать актуальный состав семьи, даже после того как сам её покинул.
- **Рекомендация:** добавить тот же чек, что и везде —
  `if (!await access.HasRoleAsync(currentUser.UserId, familyId, FamilyRole.Member, ct)) return
  Results.Forbid();` — либо на уровне эндпоинта (добавить `ICurrentUser`), либо внутри
  `FamilyService.GetFamilyMembersAsync`, приведя сигнатуру к общему виду `(familyId, userId, ct)`
  как у остальных сервисов модуля.

## 🟡 Средний приоритет

### 2. `POST /api/invites/{code}/redeem` — нет rate-limiting

> ✅ **Исправлено.** Новая политика `"invite-redeem"` (по умолчанию 1 запрос / 5 секунд на IP,
> конфигурируется через `AuthRateLimitOptions.RedeemPermitLimit`/`RedeemWindowSeconds`), навешена
> на эндпоинт `.RequireRateLimiting("invite-redeem")`. Покрыто тестом
> `AuthRateLimitTests.InviteRedeem_OverIpLimit_Returns429`.

- **Где:** `Api/Features/Invites/InviteEndpoints.cs:41-57` (группа `app.MapGroup("/api")` — не
  `/api/auth`, поэтому политика `"auth"` из `Program.cs` сюда не применяется).
- Код инвайта — 128 бит (`InviteService.GenerateCode`), так что перебор непрактичен, но это
  единственный «угадай-секрет» эндпоинт во всём приложении без вообще какого-либо rate-limit
  (в отличие от email-кодов, кодов привязки Telegram и т.д., у которых есть хотя бы IP-лимит).
  Не критично при текущей энтропии кода, но нарушает единообразие модели защиты.

### 3. `AccountService.DeleteAccountAsync` — N+1 запросы при проверке «блокирующих» семей

- **Где:** `Api/Features/Account/AccountService.cs:42-60`.
- Для каждой семьи, где состоит удаляемый пользователь, внутри `foreach` идёт отдельный запрос
  `CountAsync` на «остальных участников», и ещё один — на «остальных активных админов», если
  применимо. Для пользователя, состоящего во многих семьях, это N+1 паттерн. Не риск безопасности,
  просто производительность на удаление аккаунта (нечастая операция, но стоит держать в уме при
  росте числа семей на пользователя).

### 4. Нет лимита на количество создаваемых семей одним пользователем

> ✅ **Исправлено.** `FamilyService.MaxFamiliesPerUser = 25` — `CreateFamilyAsync` считает семьи,
> где пользователь `Role == Admin` (единственный способ получить эту роль — создание; промоушена
> в продукте нет), и возвращает `409` при достижении лимита. Отражено в UI: `families-tab`
> показывает счётчик «создано X/25» и блокирует кнопку «Создать» при лимите (константа
> `MAX_FAMILIES_PER_USER` зеркалит серверную). Покрыто юнит- и интеграционным тестом
> (`FamilyServiceTests.CreateFamilyAsync_AtLimit_...`,
> `FamiliesAndInvitesFlowTests.CreateFamily_AtLimit_Returns409_...`).

- **Где:** `Api/Features/Families/FamilyEndpoints.cs:14-23` (`POST /api/families`).
- Любой аутентифицированный пользователь может создать неограниченное число семей подряд (только
  под общим IP-лимитом `"auth"`, который на этот путь даже не распространяется — `/api/families`
  не в группе `/api/auth`). Возможный вектор спама/захламления БД, низкий приоритет.

## 🟢 Низкий приоритет / на заметку

### 5. `ConsentRequiredFilter` — позитивный кэш в `IMemoryCache`, не переживёт горизонтальное масштабирование

- **Где:** `Infrastructure/Consents/ConsentRequiredFilter.cs:30-41`, `ConsentService.cs:49-51`.
- Кэш «согласие принято» на 5 минут — локальный для процесса (`IMemoryCache`). При единственном
  инстансе (текущее развёртывание, судя по другим комментариям в коде про
  «single-instance деплой») это не проблема. Если/когда появится горизонтальное масштабирование —
  прогрев кэша при `AcceptAsync` будет виден только тому инстансу, который обработал запрос,
  остальные реплики узнают о принятии согласия только по истечении своего локального TTL (до 5
  минут задержки) или по прямому запросу в БД при промахе кэша (что само по себе не баг, просто
  небольшая рассинхронизация между репликами). Стоит держать в уме при планировании масштабирования.

### 6. `FamilyId`, попавший в клиентские данные, не «истекает» вместе с членством

- См. находку №1 — сама природа проблемы (GUID остаётся «известным» бывшим участникам навсегда) —
  общее наблюдение о модели: ни один ресурс, «привязанный» к семье через её GUID, не должен
  полагаться только на секретность GUID как на контроль доступа. Стоит перепроверить, нет ли ещё
  где-то подобных мест (в этом модуле — только находка №1; в остальных модулях проверялось отдельно,
  см. соответствующие файлы).

## ✅ Проверено, проблем не найдено

- `InviteService.RedeemInviteAsync` — редемпшн + инкремент `UsedCount` в одной транзакции
  (`BeginTransactionAsync`) — корректная защита от гонки на `MaxUses` при параллельных запросах.
- `MembershipService`/`AccountService` — guard «нельзя убрать/выйти/удалить, если это оставит семью
  без единственного активного админа» — выдержан и при выгоне (`RemoveMemberAsync`), и при
  самостоятельном выходе (`LeaveFamilyAsync`), и при удалении аккаунта (`DeleteAccountAsync`).
- `FamilyService.DeleteFamilyAsync` — корректно проверяет `FamilyRole.Admin` и явно чистит
  `FamilyMedicalShare`/`MedicalRecordHidden` (нет FK на `Family` — см. комментарий в коде,
  подтверждено проверкой конфигураций EF в `Infrastructure/Persistence/Configurations`).
- Согласия ПДн: два обязательных чекбокса (общий + спецкатегория здоровья) пишутся атомарно одним
  вызовом `AcceptAsync`, идемпотентность через `UNIQUE(UserId, Kind, Version)` с graceful-обработкой
  гонки двойного клика (`DbUpdateException` → `ChangeTracker.Clear()`).
- Право на экспорт/удаление (152-ФЗ): `AccountService.WriteExportZipAsync`/`DeleteAccountAsync`
  корректно расшифровывают поля через тот же EF-конвертер, аудит-запись `Erasure`/`Export` пишется
  в одной транзакции с самим действием.
