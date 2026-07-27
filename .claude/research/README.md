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
- [`web-miniapp.md`](web-miniapp.md) — `FamilyHub.Web`: React Mini App (Telegram-клиент), её контракт с API. **Устарел** — фронт переписан на Angular 18, см. `auth-uiux-rework-stage.md`.
- [`auth-uiux-rework-stage.md`](auth-uiux-rework-stage.md) — Auth + UI/UX rework после Stage 2 (username/Telegram-линковка/merge аккаунтов, гвард навигации, cookie-баннер), дебаг-репорт по Mini App багам, паттерны разработки для бэка и фронта (логгер, особенности PWA/Mini App). Telegram-линковка оттуда (`TelegramLinkService`/`AccountMergeService`) — легаси-путь, не заменён, см. следующий файл.
- [`navigation-redesign-and-web-push.md`](navigation-redesign-and-web-push.md) — редизайн навигации (7 табов → 4, хаб «Здоровье», серверные чипы поиска), систематизация Pages/Panels/Modals, реальный Web Push (ADR-0004), дебаг-репорт по ngsw/`ng serve`/permission-flow, контекст docker dev-стека.
- [`auth-email-anchor-jwt-rework.md`](auth-email-anchor-jwt-rework.md) — email как единственный якорь identity (без merge для новых привязок): PWA переведена на JWT-access + DB-backed refresh-сессии (`UserSession`/`TokenService`, ротация + reuse-detection), Telegram Mini App стал lookup-only (`TelegramMiniAppAuthenticationHandler` больше не авто-провижинит), новый bind-флоу `TelegramBindingService`/`/api/auth/telegram/{init,send-code,bind,revoke}`. Дебаг-репорт: `BindAsync`-коллизия имени в Minimal API, 3 теста, сломанные удалением auto-provisioning.

## Архитектура одной картинкой

Модульный монолит. Зависимости — только в одну сторону: `*.Modules.*` и `FamilyHub.Api`
зависят от `FamilyHub.Domain` и `FamilyHub.Infrastructure`, но **никогда** друг от друга
напрямую (Medical не знает о Birthdays и наоборот). Общие сквозные сервисы (доступ по ролям,
текущий пользователь, хранилище файлов, отправка оповещений) живут в Infrastructure как
абстракции и подключаются модулям через DI.

```
FamilyHub.Domain            — сущности, enum'ы, без зависимостей от EF/ASP.NET
FamilyHub.Infrastructure     — EF Core/Postgres, auth, авторизация, MinIO/Local storage,
                                Telegram (initData-валидация, bot client), оповещения
FamilyHub.Api                 — composition root (Program.cs), семьи/инвайты/участники/
                                оповещения/бот-вебхук, раздача Mini App (wwwroot)
FamilyHub.Modules.Medical     — аптечка, анализы, вложения (зависит только от Domain+Infra)
FamilyHub.Modules.Birthdays   — дни рождения (зависит только от Domain+Infra)
FamilyHub.Web                 — React Mini App, собирается в FamilyHub.Api/wwwroot
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
   `ASPNETCORE_ENVIRONMENT=Development` — никогда в проде.
