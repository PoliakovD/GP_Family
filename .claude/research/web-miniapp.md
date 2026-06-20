# `FamilyHub.Web` — Telegram Mini App

React + TypeScript + Vite SPA. Собирается **прямо в `FamilyHub.Api/wwwroot`**
(`vite.config.ts`: `build.outDir = "../FamilyHub.Api/wwwroot"`, `emptyOutDir: true`) — это не
отдельный деплоймент, а статика, которую раздаёт сам API (`app.UseStaticFiles()` +
`MapFallbackToFile("index.html")` в `Program.cs`, см. `api-core.md`). Тот же origin, что и API
→ CORS не нужен.

## Структура

- `src/telegram.ts` — обёртка над `window.Telegram.WebApp`: `initTelegram()` (`ready()`+`expand()`,
  безопасный no-op вне Telegram), `getInitData()`, `isInsideTelegram()`, `openExternalLink(url)`
  (внутри Telegram — `webApp.openLink`, снаружи — `window.open`).
- `src/api.ts` — единственная точка HTTP-вызовов к API. `authHeaders()`:
  - внутри Telegram → `Authorization: tma <initData>`;
  - снаружи (обычный браузер, локальная отладка) → `X-Dev-TelegramId` из `?devTgId=`
    query-параметра, персистится в `localStorage` (бьётся с `DevAuthenticationHandler`,
    только Development на стороне API — см. `infrastructure.md`).
  - Экспортирует типизированную функцию на каждый эндпоинт API (полный список — в
    `api-core.md` → «точная карта маршрутов»). `uploadAttachment` — отдельный `fetch` с
    `FormData` (multipart), не через общий `request<T>()`.
- `src/types.ts` — TS-зеркало backend DTO. **Важно**: enum'ы (`FamilyRole`, `MemberStatus`,
  `NotificationType`) объявлены как `const`-объекты с `as const`, не TS `enum` — в
  `tsconfig.app.json` включён `erasableSyntaxOnly`, который запрещает синтаксис, требующий
  runtime-поддержки TS (настоящие `enum`, parameter-property shorthand в конструкторах).
  Enum'ы на проводе — **обычные числа** (порядок объявления в C#, `JsonStringEnumConverter` не
  зарегистрирован), `DateOnly` — строка `"yyyy-MM-dd"`, `DateTime` — ISO 8601 с `Z` — всё
  проверено эмпирически живыми запросами к API, не предположениями.
- `src/main.tsx` — точка входа, вызывает `initTelegram()` перед рендером.
- `src/App.tsx` — вкладочная навигация (`'families' | 'medications' | 'birthdays' | 'records' | 'notifications'`),
  держит `families`/`activeFamilyId`, выпадающий список семей ограничен `MemberStatus.Active`.
- `src/components/*.tsx` — по одному компоненту на вкладку (`FamiliesTab`, `MedicationsTab`,
  `BirthdaysTab`, `MedicalRecordsTab`, `NotificationsTab`).

## Известные технические ограничения TS-конфига (важно при добавлении нового кода)

- `erasableSyntaxOnly: true` → никаких `constructor(public x: T)` и `enum X {}`; вместо
  enum — `export const X = { A: 0, B: 1 } as const`, вместо parameter-property — явное поле +
  присвоение в теле конструктора (см. `ApiError` в `api.ts`).
- `verbatimModuleSyntax: true` → типы импортировать только через `import type { T } from '...'`,
  не вперемешку со значениями в одном безусловном импорте.
- JSX-трансформ `react-jsx` → `React` не глобальный неймспейс; `React.FormEvent` не существует,
  нужно `import { type FormEvent } from 'react'` и использовать `FormEvent` напрямую (см. все
  формы в `components/*.tsx`).

## Известное ограничение функциональности

`MedicalRecordsTab` хранит список загруженных вложений (`attachmentsByRecord`) только в React
state текущей сессии — backend не даёт эндпоинта «список вложений записи» (см.
`module-medical.md` → «Известное ограничение v1», `TECH_DEBT.md` п.1). После добавления
такого эндпоинта на backend, на фронте нужно будет подгружать список при выборе записи вместо
накопления из ответов на загрузку.

## Локальная разработка без Telegram

`http://localhost:<port>/?devTgId=<любое число>` — один раз кладёт `devTgId` в `localStorage`,
дальше работает как обычная авторизованная сессия через `X-Dev-TelegramId`. Работает только
если API запущен с `ASPNETCORE_ENVIRONMENT=Development`.
