# FamilyHub — обзор модулей проекта

Индекс ресёрч-файлов с описанием архитектуры по модулям. Цель — дать будущей работе
(своей или Claude) быстрый и точный вход в проект без необходимости перечитывать весь код.
Бизнес-контекст и требования — в `.claude/FamilyHub_project_brief.md`, известные ограничения —
в `TECH_DEBT.md` (корень репозитория). Эти файлы документируют **как сделано**, бриф — **что и почему требуется**.

## Файлы

- [`domain.md`](domain.md) — `FamilyHub.Domain`: сущности, enum'ы, инварианты владения ресурсами.
- [`infrastructure.md`](infrastructure.md) — `FamilyHub.Infrastructure`: аутентификация (Telegram initData + Dev), авторизация по ролям, БД/EF Core, файловое хранилище, оповещения, Telegram-интеграция.
- [`api-core.md`](api-core.md) — `FamilyHub.Api`: семьи/инвайты/участники/оповещения, бот-вебхук, `Program.cs` (композиция всего приложения).
- [`module-medical.md`](module-medical.md) — `FamilyHub.Modules.Medical`: аптечка, анализы (персональный ресурс с шарингом), вложения.
- [`module-birthdays.md`](module-birthdays.md) — `FamilyHub.Modules.Birthdays`: дни рождения.
- [`web-miniapp.md`](web-miniapp.md) — `FamilyHub.Web`: React Mini App (Telegram-клиент), её контракт с API. **Устарел** — фронт давно переписан на Angular (сейчас 20), читать как историю; актуально — `auth-uiux-rework-stage.md`, `../patterns/frontend_web.md`, `ui-rework-v2.3.md`, `frontend-toolchain-and-e2e.md`.
- [`auth-uiux-rework-stage.md`](auth-uiux-rework-stage.md) — Auth + UI/UX rework после Stage 2 (username/Telegram-линковка/merge аккаунтов, гвард навигации, cookie-баннер), дебаг-репорт по Mini App багам, паттерны разработки для бэка и фронта (логгер, особенности PWA/Mini App). Telegram-линковка оттуда (`TelegramLinkService`/`AccountMergeService`) — легаси-путь, не заменён, см. следующий файл.
- [`navigation-redesign-and-web-push.md`](navigation-redesign-and-web-push.md) — редизайн навигации (7 табов → 4, хаб «Здоровье», серверные чипы поиска), систематизация Pages/Panels/Modals, реальный Web Push (ADR-0004), дебаг-репорт по ngsw/`ng serve`/permission-flow, контекст docker dev-стека.
- [`auth-email-anchor-jwt-rework.md`](auth-email-anchor-jwt-rework.md) — email как единственный якорь identity (без merge для новых привязок): PWA переведена на JWT-access + DB-backed refresh-сессии (`UserSession`/`TokenService`, ротация + reuse-detection), Telegram Mini App стал lookup-only (`TelegramMiniAppAuthenticationHandler` больше не авто-провижинит), новый bind-флоу `TelegramBindingService`/`/api/auth/telegram/{init,send-code,bind,revoke}`. Дебаг-репорт: `BindAsync`-коллизия имени в Minimal API, 3 теста, сломанные удалением auto-provisioning.
- [`settings-hub-and-account-security.md`](settings-hub-and-account-security.md) — `/settings` разложен на вкладки (второй хаб-компонент после «Здоровья»): смена пароля, список сессий («мои устройства») + `logout-all`, уведомления по типам (`UserNotificationPreference`, разреженное хранение). Фикс давнего бага отписки от push (не была идемпотентна, 404 вместо успеха). Дебаг-репорт: `api`-контейнер требует ребилда для любого backend-изменения (не только `.env`), прямая загрузка вложенных роутов ломается в `ng serve` (не регрессия).
- [`identity-fio-rework.md`](identity-fio-rework.md) — `User.DisplayName` заменён структурированным профилем (ФИО тремя полями + ДР + пол, `ValueObjects.PersonName`), тот же профиль на `FamilyDependent`. Напоминания о ДР — три источника (`Birthday`/`User.BirthDate`/`FamilyDependent.BirthDate`) через `BirthdaySubjectKind`. Первая брейкпойнт-абстракция на фронте (`BreakpointService`), новый `profileGuard`/`/profile-setup`.
- [`concurrency-correctness-audit-2026-08-27.md`](concurrency-correctness-audit-2026-08-27.md) — аудит гонок/N+1/несостыковок бэкенда (не security, дополняет `docs/security/module-review-2026-08-02/`): 25 находок, 13 исправлено и провалидировано на реальном Postgres (все Critical/High + часть Medium), таблица статусов + что осталось и почему. Отдельно — пойманная и исправленная в этой же сессии регрессия от наивного кэша поверх `KbLookupService` (ломал live-поллинг статуса обогащения).
- [`ai-availability-and-waiting-queue.md`](ai-availability-and-waiting-queue.md) — недоступный ИИ (LM Studio): очередь «ждём ИИ» вместо ошибок — проба + `/api/ai/status`, `WaitingForAi`, `LmStudioRecoverySweepJob` (раз в минуту), автопересчёт резюме токеном `SummaryDirtyAt`, отложенный биоматериал; что осознанно синхронно и известные пробелы. Решение — `docs/adr/0013`.
- [`ui-rework-v2.3.md`](ui-rework-v2.3.md) — доводка по макетам: Главная («Требует внимания», 3 ближайшие даты ДР, приглашение), открытая запись анализа (шапка, плитки, шкала с подписями), статья справочника (иконки норм, рамка значения); правила и грабли.
- [`frontend-toolchain-and-e2e.md`](frontend-toolchain-and-e2e.md) — Angular 18→20 (что сделала миграция, грабля `npm ci` с `@types/node`), политика по зависимостям (что заморожено и почему), e2e на Playwright (стек, вход, грабли), таблица CI-workflows.

## Архитектура одной картинкой

Модульный монолит. Зависимости — только в одну сторону: `*.Modules.*` и `FamilyHub.Api`
зависят от `FamilyHub.Domain` и `FamilyHub.Infrastructure`, но **никогда** друг от друга
напрямую (Medical не знает о Birthdays и наоборот). Общие сквозные сервисы (доступ по ролям,
текущий пользователь, хранилище файлов, отправка оповещений) живут в Infrastructure как
абстракции и подключаются модулям через DI.

```
FamilyHub.Domain            — сущности, enum'ы, без зависимостей от EF/ASP.NET
FamilyHub.Infrastructure     — EF Core/Postgres, auth, авторизация, MinIO storage,
                                Telegram (initData-валидация), LM Studio (проба/клиент), оповещения
FamilyHub.Api                 — composition root (Program.cs), семьи/инвайты/участники/
                                оповещения/бот-вебхук, раздача Mini App (wwwroot)
FamilyHub.Modules.Medical     — аптечка, анализы, вложения (зависит только от Domain+Infra)
FamilyHub.Modules.Birthdays   — дни рождения (зависит только от Domain+Infra)
FamilyHub.TelegramBot        — Telegram-бот отдельным процессом, без доступа к БД (ADR-0008)
FamilyHub.Web                 — Angular 20 SPA (PWA + Telegram Mini App), собирается в FamilyHub.Api/wwwroot
```

Каждый модуль (`*.Modules.*`) — отдельный csproj с одним статическим методом расширения
(`AddXModule()` / `MapXModule()`), который регистрируется в `Program.cs`. Новый модуль = новый
csproj + такие же два метода, без правок в существующих модулях.

## Сквозные инварианты (актуальны для любого нового кода)

1. **Семейные ресурсы** (`Medication`, `Birthday`, реализуют `IFamilyOwned`) — всегда
   фильтруются по `FamilyId`, доступ проверяется ролью через `IFamilyAccessService`. Никогда
   не грузить ресурс по `Id` без проверки членства в его семье.
2. **Личные ресурсы** (`MedicalRecord`) принадлежат пользователю, не семье. Шарингом и
   скрытием управляет только владелец — даже админ семьи не может вмешаться.
3. `MemberStatus.PendingApproval` не даёт доступа ни к чему, даже к ресурсам семьи, в которую
   человек подал заявку.
4. Файлы — только через короткоживущие presigned/signed URL (`IFileStorage`), никогда не
   статической прямой ссылкой на бакет/диск.
5. Любая Telegram-аутентификация (initData HMAC, webhook secret) проверяется **первым шагом**,
   до парсинга бизнес-данных и до вызова сервисов.
6. `DevAuthenticationHandler` (заголовок `X-Dev-TelegramId`) регистрируется только при
   `DevTools:DevAuthEnabled=true` (по умолчанию `false`; на VPS всегда выключен) — никогда в проде.
