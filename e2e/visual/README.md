# Стенд скриншотов для редизайна

Снимает экраны фронта на 1440×1000 и 390×844 **без бэкенда**: собранный SPA раздаётся статикой,
а `/api/**` отвечает Playwright из фикстур. Данные совпадают с макетами `docs/redesign/screens/png/`
(семья «Поляковы», анализ крови, дневник…), «сегодня» — 14.10.2026. Зачем это нужно и как стенд
используется по фазам, описано в `docs/redesign/PLAN.md`, раздел «Как проверяем каждую фазу».

```bash
cd e2e && npm install                                    # один раз
(cd ../src/FamilyHub.Web && npx ng build --configuration development)
node visual/shots.mjs after                              # → .artifacts/shots/after/*.png
node visual/shots.mjs after vaccin                       # только экраны, в имени которых есть «vaccin»

# «до»: сборка базового коммита в отдельном worktree
git worktree add ../.wt-base master
(cd ../.wt-base/src/FamilyHub.Web && npm ci && npx ng build --configuration development)
SPA_DIR=../.wt-base/src/FamilyHub.Api/wwwroot node visual/shots.mjs before

node visual/compare.mjs .artifacts/shots/before .artifacts/shots/after .artifacts/shots/compare до после
node visual/compare.mjs ../docs/redesign/screens/png .artifacts/shots/after .artifacts/shots/vs-mockup макет после
```

Если браузеры Playwright не скачаны под установленную версию, укажите готовый Chromium:
`CHROMIUM=/opt/pw-browsers/chromium node visual/shots.mjs …` (так в облачном окружении Claude Code).

## Файлы

| Файл | Что делает |
|---|---|
| `shots.mjs` | статический сервер + подмена API + снимки экранов из `SCREENS` (full-page) |
| `fixtures.mjs` | `SCREENS` (имя, маршрут, `only: 'desktop' \| 'mobile'`) и ответы API |
| `compare.mjs` | склейки «A \| B» одноимённых PNG из двух папок |

## Новый экран

1. Добавьте строку в `SCREENS` в `fixtures.mjs`.
2. Запустите `node visual/shots.mjs probe <имя>`. В конце прогона стенд печатает запросы, на которые
   нет фикстуры (на них он отвечает 404), а также ошибки страницы из консоли.
3. Добавьте ответы в `ROUTES`. Форму берите из `src/FamilyHub.Web/src/app/models/types.ts` (тип
   указан у метода в `services/api.service.ts`). Поля не пропускайте: компонент, которому не хватает
   поля, падает молча и остаётся на «Загрузка…».

## Ограничения

- Чат в реальном времени (SignalR) в стенде не подключается (`hub-ticket` → 503). Экран чата
  показывает историю из фикстур, а состояние «нет соединения» на снимке — особенность стенда.
- На мобильных full-page снимках фиксированный таб-бар оказывается посередине длинной страницы.
- Снимки могут отличаться на отдельные пиксели между прогонами (сглаживание шрифтов). Сравнивайте
  склейки глазами, а не `cmp`.
- Это проверка вида, а не поведения. Сценарии с настоящим API — в `e2e/tests` (нужен стек).
