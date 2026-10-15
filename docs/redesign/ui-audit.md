# UI-аудит FamilyHub.Web: инвентарь перед редизайном

Аудит от 2026-10-14, read-only (код не менялся). Снимок: `github/master` @ `a39e531`
(Merge PR #112, семейный чат), каталог `src/FamilyHub.Web/src`. Пути ниже даны относительно
`src/FamilyHub.Web/src/app/`, кроме `styles.scss` (это `src/FamilyHub.Web/src/styles.scss`).

Этот документ — **инвентарь** («из чего состоит и где разнобой»). Чек-лист исправлений UX
лежит отдельно в [`ui-ux-audit.md`](ui-ux-audit.md) (115 пунктов, все закрыты); здесь он не повторяется.

---

## Краткое резюме

1. **Стек.** Angular 20 (standalone, signals, lazy routes), SCSS, без UI-библиотеки. Иконки Phosphor
   (regular/bold/fill, 118 разных иконок, 269 `<i class="ph…">`), шрифт Source Serif 4 (400/600)
   для всего текста. Tailwind, Material и CDK не используются.
2. **Дизайн-система уже начата, но существует наполовину.** `styles.scss` (1 557 строк) содержит
   токены (44 цвета, 6 отступов, 3 радиуса, 3 тени, 9 z-слоёв) и около 140 глобальных классов
   (`.btn*`, `.card*`, `.tag*`, `.input`, `.seg`, утилиты в духе Bootstrap). Классы используются
   активно (`.btn` 343 раза, `.input` 208/210 полей). **Токены масштаба при этом почти не используются:**
   отступы через `var(--space-*)` задают 11% значений, кегли через токены — 19 из 455.
3. **Масштаб UI:** 140 компонентов и 2 директивы. 69 компонентов подключены к роутингу (~75 URL-шаблонов
   и 13 редиректов), из них 26 — админка. 101 HTML-шаблон и 39 inline-шаблонов, 67 SCSS-файлов
   (7,4 тыс. строк) и 15 компонентов с inline `styles:`. Всего разобрано 5 367 CSS-деклараций.
4. **Мёртвых компонентов нет:** каждый селектор где-то используется. На уровне маршрутов есть
   «полумёртвые»: `/consent-text` (ни одной входящей ссылки), `/birthdays` (дублирует вкладку
   «Дни рождения» семьи, ведёт туда только ссылка с главной и поиск), 9 legacy-редиректов.
   Найдены две мелкие поломки вёрстки: `gap` через неопределённый `--space-5` (п. 10) и кнопка
   «Назад» без стилей в `vaccination-person` (§4.1).
5. **Кнопки.** 515 `<button>`: 425 стилизованы глобальной системой `.btn`, **90 свёрстаны
   на месте** примерно с 45 локальными классами. Одно и то же действие «Принять дозу» сделано
   двумя разными кнопками (`.hh-take` 32px, радиус 1px, без hover; `.it-take` 44px, радиус 2px,
   с hover), FAB скопирован дословно в двух местах.
6. **Чипы, теги и бейджи — самая разношёрстная группа: ~25 вариантов.** Padding от 0 до 8×14px,
   кегль 12–15px, радиус 1 / 1,5 / 2 / 999px, белый текст задан то через `#fff`, то через `--color-bg`.
   Счётчики-бейджи сделаны пятью способами.
7. **Карточки: глобальная `.card` (98 использований) и 15 локальных карточек и плиток.** Рамка трёх
   разных «прозрачностей» (16% / 12% / 8%), фон то `paper`, то `bg`, padding от 13×15 до 20×24px,
   статусная полоса сделана двумя техниками (4px `border-left` и 3px `inset box-shadow`).
8. **Оверлеи: 7 реализаций.** Боковая панель трижды: общая `SidePanel`, `indicator-info-panel` 392px
   и `record-add-panel` 560px, причём последняя скопирована в два компонента. Поповеров 5, у них
   разные ширина, радиус и тень.
9. **Захардкоженные значения.** Чистых цветовых литералов мало (32 уникальных, 27 из них
   в dev-panel), но **74 уникальных выражения `color-mix()`** образуют скрытую вторую палитру:
   12 вариантов «текст с прозрачностью», 13 разных процентов статусной подложки. Ещё 44 уникальных
   кегля в px, 48 атомов отступов, 16 радиусов, 17 теней, 35 бордеров, 14 z-index, 12 длительностей
   анимаций, 12 медиазапросов.
10. **В токенах есть дефекты.** `var(--space-5)` не определён, поэтому `gap` в обзоре прививок
    (`vaccinations-overview.component.scss:5`) **не применяется — это реальный баг**.
    `--color-accent-dark` не определён. Fallback `var(--radius-sm, 4px/6px)` противоречит самому
    токену (1px). Не используются 8 токенов. `neutral-600` (#666262) и `neutral-700` (#605d5d)
    различаются на 9 единиц RGB: рампа сломалась после правки контраста.
11. **«Опасность» и «успех» закодированы двумя оттенками.** Ошибки форм, `btn-danger`, `alert-danger`
    и тост-ошибка используют пурпурный `accent-2`, а медицинские статусы и `.text-danger` —
    красный `status-danger`. Тост «успех» синий (`accent-700`), а статус «норма» зелёный.
12. **Тёмной темы нет совсем:** `color-scheme: light`, ни одного `prefers-color-scheme`,
    `colorScheme` из Telegram читается, но игнорируется. При этом токены на CSS-переменных,
    так что тему можно добавить без переписывания компонентов, если сначала убрать 23 `#fff`
    и прозрачности на базе `#201e1d`.
13. **Состояния неоднородны.** Загрузку показывают 6 разных спиннеров и 60 текстов «Загрузка…»,
    скелетонов нет. Пустые состояния сделаны 7 способами, причём два из них скопированы парами.
    У `.input`, `.seg-opt` и `.switch` нет стиля `disabled`. В 8 SCSS-файлах есть `cursor:pointer`,
    но нет `:hover`. Фокус-кольцо глобальное, но локально его переопределяют 5 разными способами.
14. **Доступность (беглый взгляд):** `neutral-500` как цвет текста 9 раз (2,6:1); `.card-meta`
    3,2:1; белый на `status-ok` 3,45:1, на `status-warning` 2,5:1; текст `status-warning` 2,4:1.
    Шрифт 10,5–12px в 20 местах. 5 кнопок меньше 40px плюс ~15 кнопок-тегов высотой около 25px.
    `aria-invalid` не используется ни разу, ошибки валидации не связаны с полями (`aria-describedby` 1 раз).
    Начертания 700 и 500 (13 мест) не загружены, браузер синтезирует жирность.
15. **Типографика двоится:** `.muted` (209 использований, 14px + `margin:0`) и `.text-muted`
    (215, только цвет). Семантика заголовков не совпадает с видом: `h2.card-title` и `h4.card-title`
    выглядят одинаково, `h6` используется для секций, `h1` есть только на 2 экранах из ~70.

---

## 1. Стек и структура UI

| Что | Значение | Где |
|---|---|---|
| Фреймворк | Angular 20.3 (standalone-компоненты, signals, `@if/@for`, lazy `loadComponent`) | `package.json`, `app.routes.ts` |
| Сборка | `@angular/build` (esbuild), PWA через `@angular/service-worker` | `angular.json`, `ngsw-config.json` |
| UI-библиотеки | **нет** (ни Material, ни CDK, ни PrimeNG) | — |
| Стилизация | SCSS: один глобальный `styles.scss` + component-scoped `*.component.scss` / inline `styles:` | 67 SCSS, 15 inline |
| Иконки | `@phosphor-icons/web` (3 веса подключены целиком как CSS: regular 206 / bold 45 / fill 44 использований, 118 разных иконок) | `angular.json` → `styles` |
| Шрифт | `@fontsource/source-serif-4` 400 + 600; заголовки и текст — одна гарнитура | `angular.json`, `--font-heading`, `--font-body` |
| Графики | собственные SVG: `SparklineComponent`, `TrendLineComponent`, `ReferenceScaleComponent` | `shared/` |
| PDF | `pdfjs-dist` (рендер в `PdfPreviewComponent`) | `shared/file-viewer/renderers` |
| Адаптив | `BreakpointService` (`matchMedia` 640 / 1024) + CSS `@media` | `services/breakpoint.service.ts` |
| Каркас | `AppComponent`: сайдбар ≥1024px, мобильный топбар и нижний таб-бар <1024px | `app.component.html` (253 стр.), классы `.app-*`, `.tab-bar` в `styles.scss` |

### Что уже похоже на дизайн-систему

| Слой | Где | Насколько используется |
|---|---|---|
| Цветовые токены (`--color-*`, 44 шт.: базовые, рампы neutral/accent/accent-2 по 9 ступеней, статусы, «виды» дневника) | `styles.scss:6–150` | Высоко: 1 049 обращений к `var(--color-*)` в 70+ файлах. Не используются: `accent-2-300…600`, `accent-2-900`, `accent-900`, `process-yellow` |
| Отступы `--space-1…8` (5/10/15/20/30/40) | `styles.scss` | **Низко**: 110 из 974 ненулевых значений (11%). `--space-8` не используется ни разу, `--space-5` используется, но не определён |
| Радиусы `--radius-sm/md/lg` (1/2/4px) | `styles.scss` | Средне: 57 из 97 |
| Тени `--shadow-sm/md/lg` | `styles.scss` | 24 из 41 |
| Кегли `--font-size-body/meta/record-title` | `styles.scss` | **Низко**: 19 из 455. Остальные 436 — литералы `N/17 rem`, переведённые из px в «редизайне v2.1» |
| z-слои `--z-dropdown…toast` (9 шт.) | `styles.scss` | Хорошо: 16 из 28, остальное локальные 0/1/5/10 и `9999` в dev-panel |
| `--tap-min: 44px` | `styles.scss` | 33 использования |
| Глобальные компонентные классы (`.btn*`, `.card*`, `.tag*`, `.input`, `.seg`, `.switch`, `.alert-*`, `.table`, `.overlay-*`, `.file-chip*`) | `styles.scss` | Высоко (см. §3) |
| Утилиты в духе Bootstrap (`.d-flex`, `.mb-2`, `.gap-2`, `.text-muted`…) | `styles.scss` | Высоко: `.mb-2` 254, `.d-flex` 180. Не используются: `.hr`, `.btn-lg`, `.row-danger`, `.row-cursor`, `.d-block`, `.justify-content-center`, `.me-2`, `.ms-2`, `.p-3`, `.py-2`, `.text-primary`, `.fw-normal`, `.align-middle` |
| Источник дизайна | `FamilyHub Redesign - standalone.html` (дэк, из которого портированы токены; удалён из репо в 4.8.5 — источник правды теперь DESIGN.md) | — |

---

## 2. Инвентарь экранов

Колонка «Вход» показывает, откуда на экран можно попасть. ⚠ — полумёртвый или дублирующий экран.

### Вход, онбординг, публичные

| Экран | URL | Файл | Описание | Вход |
|---|---|---|---|---|
| Вход | `/login` | `components/login/login.component.ts` | Email-код + интро-блок, юр.тексты в модалке | гард |
| Подтверждение почты | `/telegram-bind` | `components/telegram-bind/` | Привязка Telegram через email-OTP | Telegram |
| Сессия устарела | `/telegram-expired` | `components/telegram-expired/` (inline) | Тупиковый экран Telegram | только программно (`auth.interceptor.ts:159`) |
| Приглашение | `/join/:code` | `components/join-invite/` | Принятие приглашения в семью | внешняя ссылка |
| Профиль (первичный) | `/profile-setup` | `components/profile-setup/` | Имя / дата рождения | гард |
| Согласие (гейт) | `/consent` | `components/consent-gate/` | Принятие согласия на ПДн | гард |
| ⚠ Текст согласия | `/consent-text` | `components/consent-text/` (inline) | Текст согласия отдельной страницей | **ни одной входящей ссылки** (логин показывает текст в модалке) |
| Политика | `/privacy` | `components/privacy/` (inline) | Политика конфиденциальности | ссылки из футера/форм |
| Отчёт для врача (публичный) | `/r/:token` | `components/public-report/` | PDF-отчёт по токену без входа | внешняя ссылка |
| 404 | `**` | `components/not-found/` (inline) | — | — |

### Главная, семья, уведомления

| Экран | URL | Файл | Описание | Вход |
|---|---|---|---|---|
| Главная | `/home` | `components/home/` | Сводка, «Требует внимания» (`AttentionCard`), ближайшие ДР, приглашение | меню |
| Уведомления | `/notifications` | `components/notifications-tab/` | Лента уведомлений | колокольчик |
| Семьи | `/families` | `components/families-tab/` | Список семей, создание/вход | переключатель семьи, ссылки |
| Семья | `/families/:id` | `components/family-details/` | Вкладки `?tab=`: участники, **чат** (`FamilyChat`), аптечки (`MedkitsPanel`), ДР (`BirthdaysPanel`), близкие (`DependentsPanel`) | меню «Семья» |
| ⚠ Дни рождения | `/birthdays` | `components/birthdays-tab/` | Тот же `BirthdaysPanel`, что и во вкладке семьи | «Все дни рождения» на главной, поиск |

### Здоровье (`/health`, оболочка `HealthHubComponent`)

| Экран | URL | Файл | Описание |
|---|---|---|---|
| Хаб «Моё здоровье» | `/health` | `components/health-home/` | Плитки разделов (`hh-tile`), «принять сейчас» |
| Аптечка | `/health/medications` | `components/medications-tab/` | Список аптечек (`MedkitsPanel`) |
| Аптечка — детали | `/health/medications/:id` | `components/medkit-detail-page/` | `MedicationsPanel`: лекарства, сроки, фото-распознавание |
| Приём лекарств | `/health/intake` | `components/intake-page/` | Оболочка: шапка, `SidePanel`/`BottomSheet` с формой курса и напоминаниями |
| └ Сегодня | `/health/intake`, `…/dose/:doseId` | `components/intake-today/` | Дозы на сегодня, отметка приёма |
| └ Курсы | `/health/intake/courses[/:id]` | `components/intake-courses/` | Список курсов (`IntakeCourseCard`) |
| Анализы | `/health/records` | `components/medical-records-tab/` (inline) | Обёртка над `MedicalRecordsPanel` (32 файла в папке) |
| Новый анализ | `/health/records/new` | `components/record-add-page/` (inline) | Обёртка над `RecordAdd` |
| Загрузка анализов | `/health/records/batch` | `components/record-batch-add-page/` (inline) | Обёртка над `RecordBatchAdd` |
| Анализ | `/health/records/:id` | `components/record-detail-page/` (inline) | `MedicalRecordsPanel` в режиме детали |
| Приёмы врача | `/health/visits` (+ `/new`, `/batch`, `/:id`) | `components/doctor-visit-*/` (все inline) | Те же панели, другой тип записи |
| Прививки | `/health/vaccinations` | `components/vaccinations-page/` | Оболочка + `VaccinationAddModal`, `VaccinationSavedSheet` |
| └ Обзор | `/health/vaccinations` | `components/vaccinations-overview/` | Карточки «скоро / можно» по людям |
| └ Человек / серия | `…/people/:kind/:id[/series/:code]` | `components/vaccination-person/` | Таймлайн прививок |
| Дневник | `/health/notes` | `components/health-notes-tab/` | Плитки-метрики, лента, `HealthNoteForm` в sheet/panel |
| Отчёты для врача | `/health/reports` | `components/doctor-reports-tab/` | Список отчётов, `DoctorReportForm`, `DoctorReportLinkDialog` |
| Справочник | `/health/kb` → `medications`, `indicators`, `vaccines` | `components/kb-hub/`, `kb-tab/`, `kb-analyte-tab/`, `kb-vaccines-tab/` | Три вкладки справочника |
| Показатели | `/health/indicators` | `components/indicators-tab/` | Динамика показателей (`Sparkline`, `StatusChip`) |

### Настройки (`/settings`, оболочка `SettingsComponent`)

| Экран | URL | Файл | Заголовок роута / подпись в сайдбаре |
|---|---|---|---|
| Меню профиля | `/settings` | `settings/settings-menu/` | «Профиль» |
| Аккаунт | `/settings/profile` | `settings/profile/` | «Аккаунт» |
| Безопасность | `/settings/security` | `settings/security/` | «Безопасность» |
| Уведомления | `/settings/notifications` | `settings/notifications/` | ⚠ «Уведомления» vs «**Оповещения**» в сайдбаре |
| Данные | `/settings/data` | `settings/data/` | ⚠ «Данные и доступ» vs «**Данные и приватность**» в сайдбаре |

### Админка (`/admin`, `AdminHubComponent` + `AdminSectionComponent` со второй ступенью вкладок)

| Раздел | URL → компонент |
|---|---|
| Вход | `/admin/login` → `admin-login` |
| Требует внимания | `/admin/attention` → `admin-attention` |
| Одобрение | `/admin/review` → `admin-review` (+ `review-detail`, `review-sources`, `review-config`, `review-cache-picker`, `kb-history`) |
| Мониторинг | `overview` → `admin-overview`, `storage` → `admin-storage`, `services` → `admin-system` |
| Безопасность | `keys` → `admin-keys`, `credentials` → `admin-credentials`, `stats` → `admin-security-stats` |
| Настройки | `ai-model`, `pipeline-steps`, `prompts`, `web-search`, `env` → одноимённые `admin-*` |
| Операции | `jobs` (+`admin-job-panel`), `warmup`, `search-cache` (+`admin-cache-panel`), `rebuilds`, `search-calls`, `changes` |
| Справочники | `catalog/analytes \| medications \| specimens` → один `admin-catalog` |
| Legacy-редиректы | `/admin/overview`, `storage`, `system`, `keys`, `enrichment`, `pipeline` (+ `/search`, `/medications`, `/records` в основном приложении) |

**Служебное:** `DevPanelComponent` (в `app.component.html`, виден только при `devMode`) использует
собственную тёмную палитру Tailwind-оттенков (27 цветовых литералов). При редизайне его можно
не трогать, но и в дизайн-систему его значения не попадают.

---

## 3. Инвентарь компонентов

### 3.1. Общие компоненты `shared/` (по числу вхождений в шаблоны)

| Компонент | Путь | Вхождений | Где (файлы) |
|---|---|---|---|
| `LoadingSpinner` | `shared/loading-spinner/` | 35 | 23 файла (medical-records-panel ×5, kb-analyte-tab ×4, kb-tab ×3…) |
| `Avatar` (sm 28 / md 40 / lg 48) | `shared/avatar/` | 21 | 17 файлов (app, intake-today ×3, vaccinations-overview ×2…) |
| `BottomSheet` | `shared/bottom-sheet/` | 19 | 16 файлов (medical-records-panel ×3, intake-page ×2, vaccinations-page ×2, внутри `ActionMenu` и `BackgroundJobsDropdown`) |
| `SidePanel` | `shared/side-panel/` | 11 | 9 файлов (4 в админке, intake-page ×2, vaccinations-page ×2, doctor-reports, health-notes, kb-vaccines) |
| `ActionMenu` | `shared/action-menu/` | 8 | 7 файлов |
| `SearchField` | `shared/search-field/` | 8 | 7 файлов (app ×2, app-search, birthdays, notes, kb ×2, medications-panel) |
| `Modal` (400 / wide 760) | `shared/modal/` | 7 | families, login, medkits ×2, invite-modal, doctor-report-link-dialog, review-cache-picker |
| `PersonChip` | `shared/person-chip/` | 7 | 5 файлов |
| `FileViewer` | `shared/file-viewer/` (+4 рендерера) | 6 | reports, chat, record-add, batch-add, vaccination-person, attachment-list |
| `PersonName` | `shared/person-name/` | 6 | 4 файла |
| `AttentionCard` | `shared/attention-card/` | 5 | home ×3, admin-attention ×2 |
| `BackLink` | `shared/back-link/` | 4 | health-hub, medical-records-panel, record-detail-header, medkit-detail-page |
| `StatusChip` | `shared/status-chip/` | 4 | indicator-info, indicators-tab, indicator-table ×2 |
| `ReferenceScale` | `shared/reference-scale/` | 3 | indicator-info, indicator-table ×2 |
| `TrendLine` | `shared/trend-line/` | 3 | health-home ×2, health-notes-tab |
| `Expandable` | `shared/expandable/` | 2 | indicator-info, record-add |
| `InfiniteScrollSentinel` | `shared/infinite-scroll-sentinel/` | 2 | family-chat, medical-records-panel |
| `InviteModal` | `shared/invite-modal/` | 2 | family-details, home |
| `NotificationBell` | `shared/notification-bell/` | 2 | app ×2 |
| `Sparkline` | `shared/sparkline/` | 2 | indicator-info, indicators-tab |
| `PdfPreview` | `shared/file-viewer/renderers/` | 2 | file-viewer, public-report |
| `AttachmentList`, `PipelineProgress`, `ConfirmDialog`, `Toast`, `CookieBanner`, `AppNotices`, `BackgroundJobsDropdown`, `Image/Text/UnsupportedPreview` | `shared/…` | по 1 | синглтоны каркаса или единственный потребитель |
| Директивы `appClickable` / `appTrackDirty` | `shared/util/` | 7 / 4 | — |

### 3.2. Переиспользуемые фичевые компоненты (≥2 потребителя)

| Компонент | Вхождений | Потребители |
|---|---|---|
| `MedicalRecordsPanel` | 4 | records-tab, record-detail-page, doctor-visits-tab, doctor-visit-detail-page |
| `KbHistory` (admin) | 5 | cache-panel, catalog ×2, changes, review-detail |
| `VerificationBadge` (admin) | 6 | catalog ×4, review-detail ×2 |
| `TopicSwitch`, `WebSearchBanner`, `AdminPayloadEditor` (admin) | по 4 | разделы админки |
| `KbCard` | 3 | kb-tab, medical-records-panel, medications-panel |
| `IndicatorInfo` / `IndicatorInfoPanel` | 3 / 2 | kb-analyte-tab, medical-records-panel |
| `HealthNoteForm` | 3 | health-notes-tab ×2, vaccination-saved-sheet |
| `RecordAdd`, `RecordBatchAdd`, `MedkitsPanel`, `MedicationsPanel`, `BirthdaysPanel`, `DoctorReportForm`, `IntakeCourseForm`, `IntakeReminders`, `VaccinationAddModal`, `VaccinationSavedSheet`, `RecordRecognitionStatus` | по 2 | — |

### 3.3. Глобальные классы `styles.scss` — частота в шаблонах

| Класс | Использований / файлов | | Класс | Использований / файлов |
|---|---|---|---|---|
| `.btn` | 343 / 89 | | `.card` | 98 / 58 |
| `.btn-sm` | 211 / 49 | | `.card-title` | 85 / 48 |
| `.btn-secondary` | 162 / 60 | | `.card-body` | 51 / 25 |
| `.btn-primary` | 121 / 67 | | `.card-kicker` | 27 / 19 |
| `.btn-link` / `.btn-ghost` / `.btn-icon` | 40 / 36 / 36 | | `.tag` (+`-neutral` 62, `-accent` 33) | 112 / 34 |
| `.btn-danger` / `.btn-block` | 23 / 27 | | `.seg` / `.seg-opt` | 25 / 42 |
| `.btn-lg` | **0** | | `.switch` | 27 / 12 |
| `.input` | 208 / 42 | | `.alert-danger` / `.alert-info` | 88 / 22 |
| `.form-label` | 105 / 25 | | `.table` / `.table-responsive` | 28 / 24 |
| `.muted` | 209 / 53 | | `.text-muted` | 215 / 32 |

### 3.4. Свёрстано «на месте» вместо компонента

| Тип элемента | Сколько | Где (примеры) |
|---|---|---|
| **Кнопки без `.btn*`** | 90 из 515 `<button>`, ~45 локальных классов | `va-mode` ×3, `va-cal-check` ×3, `va-cal-tag` ×3, `dn-chip`, `cf-chip`, `cf-source-tile`, `hh-take`, `it-box`, `vs-action`, `reaction`, `msg-*` ×3, `picker-item`, `vx-dose`, `vx-stage-head`, `kv-card`, `indicator-card`, `rd-choice-action`, `toast-action`, `toast-close`, 8 кнопок без класса |
| **Теги-кнопки** (`<button class="tag">`, `.tag-button`) | 15 | indicators-tab, indicator-table, medical-records-panel, record-add, record-batch-add, admin-catalog ×8 |
| **Локальные карточки и плитки** | 15 семейств, 96 классов в 27 файлах | см. §4.3 |
| **Локальные боковые панели и бэкдропы** | 3 | `indicator-info-panel`, `record-add-panel` (дважды: record-add и record-batch-add) |
| **Локальные поповеры** | 3 | `filters-popover` (medical-records-panel), `app-search-dropdown`, `app-family-switch-list` |
| **Локальные спиннеры** | 4 | `recognize-spinner`, `step-spinner`, `record-batch-add-spin`, `ph-circle-notch` ×2 |
| **Локальные пустые состояния** | 7 классов | `dr-empty`, `dn-empty`, `it-empty`, `ic-empty`, `records-empty`, `chat-empty`, `hh-tile-empty` |
| **Шапки страниц** | 12+ | `dr-head`, `dn-head`, `ip-head`, `vp-head`, `hh-header`, `home-header`, `vx-header`, `medkit-header`, `record-add-header` ×2, `settings-hero`, `record-detail-topbar` |
| **Аватары** | 2 локальных | `msg-avatar` (32px, чат), `it-avatar` (intake-today), фото-плитки `photo-tile` |
| **Строки списков** | ~25 семейств `*-row` / `*-item` / `*-list` | `inbox-row`, `dr-row`, `dn-row`, `it-row`, `ir-row`, `settings-*-row` ×4, `vx-*`, `va-*` |
| **Поиск** | 2 локальных | `dn-search`, `kv-search` (рядом с общим `SearchField`) |
| **Inline `style=""`** | 192 атрибута + 30 `[style.x]` | больше всего: admin-payload-editor 16, indicator-table 16, home 14, record-detail-body 12, medical-records-panel 11. Самые частые свойства: `font-size` (62), `color` (17), `width`/`min-width` (31), `padding` (13) |

---

## 4. Дубли и разнобой

### 4.1. Кнопки — 1 система + ~12 параллельных вариантов

| Группа | Варианты (файл) | Чем отличаются |
|---|---|---|
| Основная система | `.btn` + `primary/secondary/danger/ghost/icon/link/sm/block/lg` (`styles.scss`) | Эталон: 44px, радиус 2px, 15px/600; `sm` 36px (44 на touch); `lg` не используется |
| «Принять дозу» (зелёная) | `.hh-take` (`health-home.scss:188`), `.it-take` (`intake-today.scss:69`) | 32px vs 44px; радиус 1 vs 2px; кегль 13,5 vs 15; hover отсутствует vs `color-mix(… 88%, #000)`. Белый текст на `status-ok` даёт 3,45:1 |
| FAB «Добавить» (моб.) | `.dr-fab` (`doctor-reports-tab.scss:76`), `.dn-fab` (`health-notes-tab.scss:214`) | **Идентичны построчно**, включая `bottom: calc(78px …)` и соседнее правило `padding-bottom:72px` |
| Toggle-строка | `.dr-toggle` (doctor-report-form), `.cf-advanced-toggle` (intake-course-form), `.dn-toggle-row` (health-note-form) | gap 11px / разные padding (8×14 vs нет) |
| Карточка-выбор (radio-tile) | `.va-mode` (vaccination-add-modal), `.cf-source-tile` (intake-course-form), `.dn-kind` (health-note-form), `.rd-choice-action` (review-detail) | padding 11×12 / 12×14 / 8×12 / нет; активное состояние: `accent-100` + рамка accent / `kind-color` 10% + рамка / только hover-рамка |
| Пункт меню (строка-кнопка) | `.action-menu-item`, `.background-jobs-item`, `.vs-action`, `.app-family-switch-item`, `.settings-menu-row` | padding 10×16 / 12×14 / 10×14; gap 10 / 11px; hover-подложка 6% есть не везде (у `vs-action` нет) |
| «Назад» | `BackLinkComponent` (gap 7, 600, 15px); локальная `.back-link` в `intake-courses.scss` (gap 4, вес обычный, кегль наследуется); `<button class="back-link">` в `vaccination-person.component.html:138` — **без единого стиля** (стили `BackLinkComponent` инкапсулированы, локального правила нет), то есть рисуется нативной кнопкой браузера; `btn btn-ghost` в family-details | 4 вида одной ссылки «Назад», один сломан |
| Кнопка в тосте | `.toast-action` / `.toast-close` | 36px, радиус `var(--radius-sm, 6px)` (fallback ≠ токену), вес 700 (не загружен) |
| Шкала 1–10 | `.dn-scale-btn` | Активный цвет `kind-symptom` + `#fff` |
| Иконочная на тёмном фоне | `file-viewer.scss:74` переопределяет `.btn-icon` (`#fff`, hover `#fff 15%`) | Нет инверсного варианта в системе |
| Toggle-состояние `.btn.active` | `record-detail-header.scss:22` (`inset 0 0 0 1px accent`) | Нет pressed/toggle-варианта в системе |
| Сегмент как `<button>` | `.seg-opt` сброшен заново в 5 файлах: admin-catalog, admin-changes, admin-payload-editor, admin-review, health-notes-tab | Глобальный `.seg-opt` рассчитан на `<label>`+radio; для `<button>` каждый копирует один и тот же сброс `font/background/border` |

### 4.2. Чипы, теги, бейджи — ~25 вариантов

**Статусные (только отображение):**

| Вариант | Файл | padding | кегль | радиус | цвета |
|---|---|---|---|---|---|
| `.tag` (+`-ok/-warning/-danger/-accent/-accent-2/-neutral/-outline`) | `styles.scss` | 3×10 | 13px | 1,5px (`md*0.75`) | статус 16–18% на paper / `*-text` |
| `StatusChip` | `shared/status-chip` | 3×9 | 12px / **700** | 1,5px | своя карта ok/warning/danger/unknown |
| `.dr-chip-*` (active/expired/revoked/none) | doctor-reports-tab | 5×11 | 14px | наследует | accent-100 / surface / accent-2 10% / dashed |
| `.ic-badge` (+miss/stock) | intake-courses | 2×8 | 13px | — | neutral-200 |
| `.dn-badge` | health-notes-tab | 3×10 | 13,5px / 600 | — | kind-symptom 12% |
| `.home-ok-chip` (+empty/danger) | home | 8×14 | 15px (meta) | 2px | surface |
| `.badge-ai` | indicator-table | 0×4 | 12px / 600 | `var(--radius-sm, 4px)` | accent-100 / accent-700 |
| `.record-status-tile-*` | record-detail-header | 12×14 | — | 2px | плитка, не чип, но с той же семантикой ok/bad/none |

**Счётчики:**

| Вариант | Размер | Фон / текст | Радиус | Кегль |
|---|---|---|---|---|
| `.nav-badge` | 1.25rem | `status-danger` / `--color-bg` (4,0:1) | 999px | 12px / 600 |
| `.app-sidebar-badge` | min 20px | `accent-2` / `#fff` | 1px | **12px в px** / 700 |
| `.notification-bell-badge` | 16px | `accent-2` / `#fff` | 999px | 12px |
| `.background-jobs-trigger-badge` | 16px | `accent-2` / `#fff` | 999px | **13px в 16px-кружке** |
| `.hh-badge` (+danger) | — | статус / `#fff` | 1px | 13px |
| `.tab-bar-dot` | 8px точка | `accent-2` | 50% | — |

**Выбираемые (filter/choice chips):**

| Вариант | Файл | padding | кегль | активное состояние |
|---|---|---|---|---|
| `.dn-chip` (+sm) | health-note-form | 8×13 | 14,5px | accent + `--color-bg` |
| `.cf-chip` | intake-course-form | 6×13 | 14px | accent + **`#fff`** |
| `.va-chip` (с аватаром) | vaccination-add-modal | 5×12×5×5 | 14px | `va-chip-selected` |
| `.va-cal-tag` | vaccination-add-modal | 3×9 | 13px | `va-cal-tag-selected` |
| `PersonChip` (с аватаром) | shared/person-chip | 3×10×3×3 | 15px | `person-chip-selected` |
| `.kb-related-chip` (+link) | indicator-info | 5×11 | 15px | — |
| `.vx-file-chip` | vaccination-person | 5×10 | 13px | — |
| `.chip` | family-chat | 2×4×2×8 | 14px | — (радиус **999px**, единственный «пилюльный» чип) |
| `<button class="tag">`, `.tag-button` | 6 файлов | 3×10 | 13px | высота ~25px: недостаточная область нажатия |

### 4.3. Карточки и плитки — глобальная `.card` + 15 локальных

| Вариант | Файл | Фон | Рамка | padding | Особенности |
|---|---|---|---|---|---|
| `.card` | `styles.scss` | paper | **нет** | 15 | gap 10; `.elev-*` для тени (10 исп.) |
| `.hh-tile` | health-home | paper | divider (16%) | 20×22 | hover: рамка `accent-400` |
| `.dn-tile` | health-notes-tab | paper | text **12%** | 14×16×12 | фикс. ширина 200px на десктопе |
| `.cc-tile` (+low) | intake-course-card | **bg** | text **8%** | 13×15 | low: warning 10% на bg |
| `.it-card` (+alert/stock) | intake-today | paper | text 12% | 16×18 | — |
| `.ic-card` | intake-courses | paper | text 12% | **20×24** (моб. 16) | — |
| `.vo-card` (+duesoon/cando) | vaccinations-overview | paper | divider | 14×16 | gap 10 |
| `.kv-card` | kb-vaccines-tab | paper | divider | 14×16 | hover: рамка `accent 50%` |
| `.medkit-card` (+open) | medkits-panel | — | divider | — | open: рамка accent + shadow-sm |
| `.medication-card` (+expiry-*) | medications-panel | статус 6–8% на paper | — | — | статус-полоса `border-left 4px` |
| `AttentionCard` | shared/attention-card | paper | divider | 20 (`space-4`) | `border-left 4px` |
| `.record-status-tile` | record-detail-header | — | divider | 12×14 | — |
| `.cf-card`, `.cf-box` | intake-course-form | — | — | — | — |
| `.pr-card` | public-report | — | — | — | — |
| `.indicator-card` | indicator-table | — | — | — | кнопка-карточка |
| `.family-member-card` | family-details | (`.card`) | — | — | gap 16 |

Статусную полосу делают двумя техниками: `border-left: 4px` (`medication-card`, `attention-card`, `.ai-banner`)
и `box-shadow: inset 3px 0 0` (`.row-cursor` и ещё 5 мест, включая `--kind-color` в дневнике и `status-danger`).

### 4.4. Оверлеи — 7 реализаций + 5 поповеров

| Вариант | Файл | Фон панели | Радиус | Тень | Размер | Бэкдроп |
|---|---|---|---|---|---|---|
| `Modal` | shared/modal (`.overlay-card`) | **bg** | lg 4px | lg | 400 / 760 | `.overlay-backdrop` neutral-900 55% |
| `ConfirmDialog` | shared/confirm | bg | lg | lg | 400 | глобальный `.overlay-backdrop`, поднят до `--z-dialog` |
| `BottomSheet` | shared/bottom-sheet | bg | lg сверху | lg | 100% | `.sheet-backdrop` 55% (копия значения) |
| `SidePanel` | shared/side-panel | **paper** | 0 | lg | задаётся потребителем | `.overlay-backdrop` |
| `indicator-info-panel` | indicator-info | paper | 0 | lg | **392px** | своя `.indicator-info-backdrop` |
| `record-add-panel` | record-add **и** record-batch-add (копия) | paper | 0 | lg | **560px** | своя `.record-add-backdrop` |
| `FileViewer` | shared/file-viewer | neutral-900 | — | — | fullscreen | свой |
| `.action-menu-popover` | shared/action-menu | paper | md 2px | — | min 200px | — |
| `.background-jobs-popover` | shared/background-jobs-dropdown | paper | md | — | min 280px | — |
| `.filters-popover` | medical-records-panel | — | — | lg | 320px | — |
| `.app-search-dropdown` | app-search | paper | md | — | — | — |
| `.app-family-switch-list` | `styles.scss` | paper | md | **md** | по ширине | — |

### 4.5. Загрузка — 6 спиннеров

| Вариант | Где | Размер | Длительность |
|---|---|---|---|
| `LoadingSpinner` (круг + текст «Загрузка…», 13px) | 35 мест | 18px | 0.7s |
| `.pipeline-step-spinner` | `styles.scss` + **повторно** `@keyframes pipeline-spinner-rotate` в `pipeline-progress.scss` | 12px | 0.7s |
| `.recognize-spinner` | record-recognition-status (inline) | — | своя keyframe |
| `.step-spinner` | medications-panel | — | своя keyframe |
| `record-batch-add-spin` | record-batch-add | — | своя keyframe |
| `ph-circle-notch` (иконка) | 2 места | — | — |
| Просто текст «Загрузка…» / «Загружаем…» | 50 + 10 строк | — | — |

Скелетонов нет нигде.

### 4.6. Пустые состояния — 7 способов

| Вариант | Файлы | Вид |
|---|---|---|
| `.dr-empty` = `.dn-empty` | doctor-reports-tab, health-notes-tab | **копия**: dashed-рамка, paper, padding 40×20, gap 8 |
| `.it-empty` = `.ic-empty` | intake-today, intake-courses | **копия**: без рамки, padding 40×16, gap 10 |
| `.card.records-empty` | medical-records-panel | `.card` + gap 5 |
| `.chat-empty` (+icon) | family-chat | padding 30×15, иконка |
| `.hh-tile-empty` | health-home | только текст 15px neutral-600 |
| `p.muted` / `li.text-muted` | ≥ 9 мест | однострочник |
| `@empty {}` без разметки-заглушки | часть из 22 `@empty` | — |

### 4.7. Шапки страниц

Пять копий одного паттерна «заголовок + действия» с `gap: var(--space-3)`: `.dr-head`, `.dn-head`,
`.ip-head`, `.vp-head`, `.hh-header`. Отдельные варианты: `.home-header`, `.vx-header` (gap 14),
`.medkit-header` (gap 8), `.record-add-header` (×2), `.settings-hero`, `.record-detail-topbar`.
Сами заголовки: h2 18 / h3 21 / h4 9 / h5 11 / h6 4 / **h1 2** (логин и admin-hub), уровень
выбирается по виду, а не по иерархии (`h2.card-title` ≡ `h4.card-title`, `h6.kb-section-title`).

### 4.8. Вспомогательный («приглушённый») текст — 6 способов

| Способ | Использований | Цвет / размер | Контраст на paper |
|---|---|---|---|
| `.muted` | 209 | neutral-600, **14px, margin 0** | 5,4:1 |
| `.text-muted` | 215 | neutral-600, размер наследуется | 5,4:1 |
| `.card-meta` | 3 | text **50%** | **3,2:1** |
| `.card-body` | 51 | `opacity: 0.8` | 8,5:1 |
| `.field-hint-muted` | 11 | neutral-600 | 5,4:1 |
| `color: var(--color-neutral-500)` локально | 9 | #9b9797 | **2,6–2,8:1** |
| `color: var(--color-neutral-700)` локально | 38 | #605d5d ≈ neutral-600 | — |

### 4.9. Алерты и баннеры — глобальных 2, локальных ≥ 9

Глобальные: `.alert-danger` (accent-2-100/800), `.alert-info` (accent-100/800). **Нет `alert-warning` и `alert-ok`**,
поэтому их делают на месте: `.ai-banner` (warning, 4px-полоса), `.app-notice(-warn)` (inline), `WebSearchBanner`,
`.settings-push-banner` и `.settings-menu-push-banner` (два похожих баннера в соседних экранах настроек),
`.cf-warn`, `.rd-warn`, `.dr-hint-warn`, `.dn-tile-caption-warn`, `.hh-tool-sub-warn`.

### 4.10. Семантика цвета двоится

| Смысл | Вариант A | Вариант B |
|---|---|---|
| Ошибка / опасность | `accent-2` (пурпурный): `.btn-danger`, `.alert-danger`, `.field-hint-error`, тост-ошибка, удаление в `ActionMenu` | `status-danger` (красный): `.text-danger`, `.tag-danger`, `.nav-badge`, `.record-action-danger`, медицинские статусы |
| Успех | `accent-700` (синий): тост-успех, `.field-hint-ok` | `status-ok` (зелёный): `.tag-ok`, «Принять», статус «норма» |
| Бейдж-счётчик | `accent-2` (колокольчик, задачи, сайдбар, точка таб-бара) | `status-danger` (`.nav-badge`) |

---

## 5. Захардкоженные значения

> Счёт по всем 5 367 CSS-декларациям (SCSS + inline `styles:` + `style=""`). Декларации внутри
> `:root {}` в `styles.scss` (определения токенов) считаются отдельно и в таблицы «использования» не входят.

### 5.1. Цвета

**Сводка:** 44 цвета определены как токены. Вне токенов 32 уникальных литерала (27 из них только в
dev-panel), 74 уникальных `color-mix()` (123 вхождения) и 7 hex-литералов в TS-логике.

Литералы на продуктовых экранах (dev-panel исключён):

| Значение | Вхождений | Где | Что это на самом деле |
|---|---|---|---|
| `#fff` | 23 | 12 файлов (health-home, health-note-form, intake-course-form, intake-today, toast, file-viewer, badges, `styles.scss`…) | нужен токен `--color-on-accent` / `--color-on-status`; сейчас рядом используется и `--color-bg` в той же роли |
| `#000` | 4 | intake-today (`color-mix(… 88%, #000)`), file-viewer | затемнение hover, оверлей просмотрщика |
| `#7d7979` | 1 | `styles.scss` (плейсхолдер `.input`) | старое значение `neutral-600`, не токен |
| `#2d2b2b` | 2 (+2 в color-mix) | public-report | = `--color-neutral-900` |
| `#e2e0df` | 1 | public-report | ≈ `neutral-200` #eae7e7 / `surface` #eae9e9 |
| `#d97700` | 1 | public-report | = `--color-kind-wellbeing` (дубль значения) |
| `#8a5a00` | 1 | vaccination-add-modal | ≈ `status-warning-text` #8a5200 ≈ `kind-wellbeing-text` #8a4c00 |
| `#aa0b56` | 1 | pipeline-progress | = `--color-accent-2-700` |
| `rgba(255,255,255,.16)` | 1 | toast | hover на тёмном |
| TS: `#2f9e44`, `#f08c00` ×2, `#e03131`, `#9c36b5` | 5 | `shared/sparkline/sparkline.component.ts` | дубли `--color-status-*` в коде |
| TS: `#f3f2f2` ×2 | 2 | `services/telegram.service.ts` (+ `index.html` theme-color) | = `--color-bg` |

**Почти одинаковые оттенки** (расстояние в RGB):

| Пара | Δ | Вывод |
|---|---|---|
| `#eae9e9` surface ↔ `#eae7e7` neutral-200 | 2,8 | один цвет |
| `#f3f2f2` bg ↔ `#f8f4f4` neutral-100 | 5,7 | один цвет |
| `#8a5200` warning-text ↔ `#8a4c00` wellbeing-text ↔ `#8a5a00` литерал | 6–8 | один цвет |
| `#666262` neutral-600 ↔ `#605d5d` neutral-700 | 9,3 | рампа сломана: 600 подтёмнили под контраст, 700 не сдвинули |
| `#006f94` accent ↔ `#006786` accent-700 | 16,1 | акцент фактически = 700-я ступень |
| `#e2e0df` литерал ↔ `#eae7e7` neutral-200 | 13,3 | один цвет |
| `#d97700` wellbeing ↔ `#f08c00` status-warning | 31 | два «оранжевых» с разным смыслом, визуально близки |

**Скрытая палитра в `color-mix()` (топ):**

| Шаблон | Варианты процентов | Вхождений |
|---|---|---|
| `color-mix(text N%, transparent)` — линии, hover, подложки | 4, 5, 6, 7, 8, 10, **12**, 18, 24, 26, 50, 60 | 41 |
| `color-mix(status-warning N%, paper\|bg\|surface)` | 8, 10, 12, 14, 16, 18, 22, 30 | 17 |
| `color-mix(status-danger N%, paper\|surface\|transparent)` | 6, 7, 8, 9, 12, 14, 16, 22 | 14 |
| `color-mix(status-ok N%, paper\|neutral-200)` | 6, 7, 9, 14, 16, 18, 20, 22, 32 | 13 |
| `color-mix(accent N%, paper\|transparent)` | 5, 6, 8, 10, 12, 18, 30, 50 | 13 |
| `color-mix(neutral-900 55\|65%)` — бэкдропы | 55, 65 | 4 |
| `color-mix(#fff\|#000 N%)` | 15, 25, 30 / 35, 45, 60 | 6 |

Использование токенов (топ): `accent` 161, `divider` 125, `paper` 124, `neutral-600` 104, `text` 67,
`status-warning` 38, `neutral-700` 38, `bg` 37, `status-danger` 32, `status-ok` 32.

**Дефекты токенов:** не определены `--space-5` (**баг**: `vaccinations-overview.component.scss:5`) и
`--color-accent-dark` (`home.component.scss:31`, срабатывает fallback). Fallback `var(--radius-sm, 4px)` и
`var(--radius-sm, 6px)` противоречат токену (1px). Не используются: `accent-2-300/400/500/600/900`,
`accent-900`, `process-yellow`, `space-8`.

### 5.2. Шрифты

| Параметр | Уникальных | Значения (кол-во) |
|---|---|---|
| Семейства | 2 + 4 моно-стека | `var(--font-heading)` 57, `var(--font-body)` 2, `monospace` 5, три разных `ui-monospace, …` стека (4 + 1 + 1). Heading и body сейчас одна гарнитура |
| Начертания | 6 | 600 ×98, **700 ×12** (5 файлов), 400 ×6, токен ×6, **500 ×1**, normal ×1. Загружены только 400/600, поэтому 700/500 синтезируются или подменяются |
| Кегли | **44 разных px-размера** (53 разных записи) | см. ниже; только 19 из 455 через токены |
| line-height | 14 | 1 ×13, 1.2 ×7, 1.25, 1.3, 1.5 ×6, 1.45 ×3, 1.55, 1.15, `16px` ×2, 1.35, 1.6, 1.14, 1.12, 1.4 |
| letter-spacing | 9 | uppercase-«кикеры» тремя значениями: 0.08em ×9, 0.1em ×7, 0.12em ×6; заголовки −0.015 / −0.02em |

Кегли, пересчитанные в px (1rem = 17px — так их и переводили):

| px | Вхожд. | | px | Вхожд. | | px | Вхожд. |
|---|---|---|---|---|---|---|---|
| 10.5 | 1 | | 16.5 | 6 | | 22.0–22.5 | 14 |
| 11.0 | 2 | | **17.0** | 14 | | 23.0–24.6 | 7 |
| 11.9 | 2 | | 17.5–18.0 | 13 | | 25.0–26.0 | 9 |
| 12.0 | 11 | | 18.7 | 5 | | 27.2–28.0 | 2 |
| **13.0** | **136** | | 19.0 (record-title) | 7 | | 30.0–30.6 | 4 |
| 13.5 | 20 | | 19.5 | 6 | | 32.0 | 2 |
| **14.0** | **75** | | **20.0** | 15 | | 34.0 | 2 |
| 14.5 | 13 | | 20.4 | 1 | | 37.4 | 1 |
| **15.0** | **47** | | 21.0–21.3 | 10 | | 40.8–42.0 | 6 |
| 15.3 | 2 | | | | | 56.0 | 1 |
| 16.0–16.1 | 16 | | | | | | |

Ступени по 0,5px (13 / 13,5 / 14 / 14,5 / 15) и «круглые» rem последних фич (1.05, 1.1, 1.15, 1.25, 1.3 rem
не делятся на 17) показывают, что шкалы нет: каждый экран подбирал размер сам.

### 5.3. Отступы и размеры

**Отступы** (`margin/padding/gap`, разложены на атомарные значения): 1 169 значений, 48 уникальных.
`0`/`auto` — 195, токены — 110 (11% ненулевых), литералы — 864.

| Значение | Вхожд. | | Значение | Вхожд. | | Значение | Вхожд. |
|---|---|---|---|---|---|---|---|
| **8px** | 134 | | 5px | 23 | | 24px | 7 |
| **10px** | 113 | | 20px | 21 | | 15px | 5 |
| **12px** | 86 | | 3px | 20 | | 28px | 5 |
| **4px** | 77 | | 11px | 20 | | 32px | 4 |
| **6px** | 72 | | 7px | 17 | | −8px | 4 |
| **16px** | 66 | | 13px | 15 | | 1.5rem | 4 |
| **14px** | 59 | | 18px | 14 | | 72/96/43/26/22/17px | по 1–2 |
| 2px | 44 | | 9px | 14 | | `var(--space-2/3/4/1/6)` | 40/28/25/12/2 |
| | | | 40px | 9 | | `var(--space-5)` (**не определён**) | 1 |

Реально используемый ритм кратен 2/4 (4, 6, 8, 10, 12, 14, 16), а токены кратны 5 (5/10/15/20/30/40).
Шкала токенов не совпадает с практикой, поэтому её и обходят.

**Ширины / высоты:** 79 / 64 уникальных значения. Повторяющиеся ширины панелей и контента без токенов:
200px ×8, 220 ×4, 240 ×2, 260 ×2, 320 ×5, 360, 392, 400, 420 ×3, 460 ×3, 560 ×2, 720 ×2, 760 ×4.
Высоты интерактивных элементов: `var(--tap-min)` ×31, 36px ×7, 38px ×3, 32px ×2.

### 5.4. Радиусы, тени, бордеры, z-index, анимации, брейкпоинты

| Категория | Уникальных | Распределение |
|---|---|---|
| **border-radius** | 16 | `var(--radius-md)` 39, `50%` 21, `999px` 7, `radius-lg` 7, `radius-sm` 5, `4px` 3, `2px` 3, `calc(md*0.75)`=1,5px 2, fallback-варианты 3, `8px` 2, `10px`, `11px`, `12px 12px 12px 4px` / `12px 12px 4px 12px` (пузыри чата) |
| **box-shadow** | 17 | токены sm/md/lg 24; «полоса слева» `inset 3px 0 0 …` ×6; фокус-кольца 5 вариантов (`0 0 0 3px accent-100`, `0 0 0 3px accent-300`, `inset 0 0 0 2px accent`, `inset 0 0 0 2px accent-800`, `inset 0 0 0 1px accent`); 2 тени на литералах `#2d2b2b` (вне токенов); 2 в dev-panel |
| **border** | 35 | `1px solid divider` ×103; `text 12%` ×14 и `text 8%` ×8 (альтернативные «divider»); `control-border` всего ×2 вне `styles.scss`; dashed ×5 вариантов; полосы 3px/4px ×8 |
| **z-index** | 14 | токены 16; локальные `1` ×6, `0` ×2, `5`, `10` ×2 (топбары в `styles.scss:1275,1304`), **`9999`** (dev-panel) |
| **opacity** | 9 | 0.6 ×6, 0.75 ×3, 0.8 ×2, 0.85, 0.5, 0.3, 0.25 ×2 (disabled: 0.5 у `.btn`, 0.6 у `.btn-link`) |
| **Длительности** | 12 | 0.15s ×7, 0.2s ×5, 0.25s ×5, 0.7s ×5 (спиннеры), 0.05s ×2, 0.12s, 0.18s, 0.22s, 0.4s, 1s, 1.2s; easing в основном `ease`/`linear` без токенов |
| **@media** | 12 | `max-width:1023px` ×16, `max-width:639px` ×6, `min-width:1024px` ×3, `max-width:640px` ×2 (639 и 640 одновременно), **выбросы:** 479, 599, 900 ×2, 1100, 1200; плюс `pointer:coarse`, `hover:hover`, `prefers-reduced-motion` ×3 |

---

## 6. Состояния и паттерны

| Состояние | Как сделано | Где нет / проблемы |
|---|---|---|
| **Загрузка** | `LoadingSpinner` ×35 (центрированный круг + «Загрузка…»), 5 других спиннеров, 60 текстовых «Загрузка…/Загружаем…», `PipelineProgress` для распознавания | Нет скелетонов, `aria-busy` 0 раз; кнопки без собственного loading-состояния (`[disabled]` + смена текста) |
| **Пустые** | 7 способов (§4.6); 22 блока `@empty` | Иконка, текст и CTA в каждом месте свои |
| **Ошибки** | `.alert-danger` 91, `toast.error()` 185 вызовов, `.field-hint-error` 7, `.input-warn/.input-diff` (одна форма), «Повторить» 15 кнопок | `aria-invalid` 0, `aria-describedby` 1; цвет ошибки двоится (§4.10); `role="alert"/aria-live` — 34 места на весь UI |
| **disabled** | `.btn:disabled` (opacity .5, `not-allowed`), `.btn-link:disabled` (.6), `[disabled]=` 206 привязок | **Нет стилей** для `.input:disabled`, `.seg-opt`, `.switch`, `.tag-button`, чипов (`dn-chip`, `cf-chip`…); вид отличается только нативным браузерным |
| **hover** | глобально у `.btn-*`, `.seg-opt`, сайдбара, `.table`; 43 `:hover` в компонентах | 8 SCSS с `cursor:pointer` без `:hover`: admin-catalog, doctor-report-form, health-notes-tab, intake-reminders, medications-panel, medkits-panel, vaccination-saved-sheet, shared/expandable. `.btn-secondary` и `.btn-danger` без `:active` |
| **pressed/active** | `.btn-primary:active`, `.btn-ghost:active`, `.card-clickable:active` (opacity .85), `.file-chip-add:active` | Остальные без нажатого состояния; toggle-кнопки решают сами (`.btn.active`, `.dn-chip.active`, `.seg-opt.active`) |
| **focus** | глобальный `:focus-visible { outline: 2px accent }` | Локально переопределяется 5 разными box-shadow/outline (§5.4); `outline:none` у `.side-panel` и `.overlay-card:focus` (контейнеры, допустимо) |
| **Выбранное** | `.active` (54 использования), `*-selected`, `*-checked`, `:has(input:checked)` | 4 разных визуала выбранного: заливка accent + текст bg/#fff; accent-100 + рамка; `kind-color` 10% + рамка; inset-рамка |
| **Тёмная тема** | **отсутствует**: `color-scheme: light`, `prefers-color-scheme` 0, Telegram `colorScheme` игнорируется (`telegram.service.ts:6`) | Блокеры: 23 `#fff`, 41 `color-mix(text …, transparent)` (на тёмном станут светлыми, это нормально), литералы `#2d2b2b`, `#000`, тени на `#2d2b2b`; тема для `theme-color` захардкожена в `index.html` и `telegram.service.ts` |
| **Reduced motion** | глобальный сброс в `styles.scss` (кроме `*spin*`) + 2 локальных | ок |

---

## 7. Доступность (беглый взгляд)

| Проблема | Где | Деталь |
|---|---|---|
| Низкий контраст текста | `color: var(--color-neutral-500)` ×9: family-chat:221, health-home:72/134/279, health-note-form:102/225, health-notes-tab:71, medkits-panel:46, `styles.scss:1131` (`.app-sidebar-group-label`) | 2,59:1 на bg, 2,77:1 на paper (нужно 4,5) |
| | `.card-meta` (text 50%) | 3,21:1 |
| | плейсхолдер `#7d7979` на surface | 3,55:1 |
| | `color: var(--color-status-warning)` — settings-menu:29; `status-ok` / `status-danger` как текст — record-batch-add:69/73 | 2,38 / 3,30 / 4,33:1 |
| | белый на `status-ok` (`.hh-take`, `.it-take`, `.hh-badge`) | 3,45:1 |
| | белый на `status-warning` (если используется в бейджах) | 2,48:1 |
| Мелкий текст | 10,5–12px в 20 местах: dev-panel (10,5/11/12), family-chat:134 (11px), `font: 600 0.6471rem` (11px) в vaccination-add-modal:109, vaccination-person:97, vaccinations-overview:8; 12px: status-chip, avatar-sm, бейджи, reference-scale, indicator-table:132, medications-panel:29/142; 0.7rem в admin-review | Аудитория включает пожилых, базовый кегль 17px подобран под них |
| Маленькие области нажатия | `.va-cal-check` 22px, `.hh-take` 32px, `.it-box` 32×32, `.toast-close` 36×36, `.toast-action` 36px, dev `.dp-fab` 36px; кнопки-теги (`<button class="tag">` ×7, `.tag-button` ×8, `.va-cal-tag` ×3, `.vx-file-chip`) около 25–30px | `.btn-sm` 36px на десктопе (на touch 44), это сознательно |
| Иконки без подписи | 1 кнопка только с иконкой без `aria-label`: `vaccination-add-modal.component.html` (`removeCertFile`); 5 `<i class="ph…">` без `aria-hidden` из 269 | в остальном хорошо: `aria-label` 170, `aria-hidden` 329 |
| Формы | `<label for>` 91 при 105 `.form-label`; `aria-invalid` 0; `aria-describedby` 1 | ошибки валидации не связаны с полями для скринридеров |
| Заголовки | `h1` на 2 экранах; уровень выбирается по виду | навигация скринридера по заголовкам неинформативна |
| Синтетический жирный | `font-weight: 700` ×12, `500` ×1 при загруженных 400/600 | 700 синтезируется браузером (жирнее и грязнее), 500 рендерится как 400 |
| Изображения | 6 `<img>`, у всех есть `alt` | ок |

---

## Кандидаты в дизайн-систему

### Минимальный набор базовых компонентов

| Компонент DS | API (черновик) | Что заменяет |
|---|---|---|
| **Button** | `variant: primary \| secondary \| ghost \| danger \| success \| link \| inverse`, `size: sm \| md \| lg`, `icon-only`, `loading`, `pressed` (toggle) | `.btn*` (оставить как основу), `.hh-take`, `.it-take`, `.toast-action`, `.dp-btn`, `file-viewer .btn-icon`-override, `.btn.active` из record-detail-header, `.dr-toggle`/`.cf-advanced-toggle` (как `ghost` + chevron) |
| **IconButton** | `size`, `label` (обязательный aria) | `.btn-icon`, `.toast-close`, `.photo-remove`, `.file-chip-remove`, `.viewer-nav-arrow` |
| **Fab** | `label`, `icon` | `.dr-fab`, `.dn-fab` |
| **BackLink** | — | `BackLinkComponent` (уже есть) + локальные `.back-link` ×2 + `btn-ghost` «Все семьи» |
| **Badge** (только отображение) | `tone: neutral \| info \| ok \| warning \| danger \| none \| brand`, `variant: soft \| solid \| outline`, `icon` | `.tag*`, `StatusChip`, `.dr-chip-*`, `.ic-badge`, `.dn-badge`, `.home-ok-chip`, `.badge-ai`, `.hh-badge` |
| **CounterBadge / Dot** | `count`, `tone` | `.nav-badge`, `.app-sidebar-badge`, `.notification-bell-badge`, `.background-jobs-trigger-badge`, `.tab-bar-dot` |
| **Chip** (выбор / фильтр) | `selected`, `leading` (avatar/icon), `removable`, `size` | `.dn-chip`, `.cf-chip`, `.va-chip`, `.va-cal-tag`, `PersonChip`, `.kb-related-chip`, `.vx-file-chip`, `.chip` (чат), `<button class="tag">`, `.tag-button` |
| **ChoiceTile** (radio/checkbox-карточка) | `selected`, `icon`, `title`, `subtitle`, `accent` | `.va-mode`, `.cf-source-tile`, `.dn-kind`, `.rd-choice-action`, `.dn-scale-btn` (как компактный вариант) |
| **SegmentedControl** | `options`, `value` (работает и на `<button>`, и на radio) | `.seg/.seg-opt` + 5 локальных сбросов, `.dr-seg`, `.dn-seg`, вкладки `AdminSection` |
| **Switch / Checkbox** | — | `.switch`, `.checkbox-row`, `.va-cal-check`, `.it-box` |
| **Field** (Input / Select / Textarea + Label + Hint/Error) | `label`, `hint`, `error` → `aria-invalid` + `aria-describedby`, `disabled` | `.input` + `.form-label` + `.field-hint*`, `.input-warn/.input-diff`, `.dr-field`, `.dn-field`, `.cf-field`, `.composer-input` |
| **SearchField** | — | `SearchField` (уже есть) + `.dn-search`, `.kv-search` |
| **Card** | `variant: plain \| outlined \| interactive`, `tone` (статус-полоса), `padding: sm \| md \| lg` | `.card`, `.hh-tile`, `.dn-tile`, `.cc-tile`, `.it-card`, `.ic-card`, `.vo-card`, `.kv-card`, `.medkit-card`, `.medication-card`, `AttentionCard`, `.record-status-tile`, `.cf-card`, `.pr-card`, `.indicator-card` |
| **StatTile** (метрика) | `label`, `value`, `caption`, `tone`, `trend` | `.hh-tile-value*`, `.dn-tile-*`, `.record-status-tile-*`, `.cc-tile-*` |
| **ListItem / Row** | `leading`, `title`, `subtitle`, `trailing`, `interactive` | `.action-menu-item`, `.background-jobs-item`, `.vs-action`, `.app-family-switch-item`, `.settings-*-row` ×4, `.dr-row`, `.dn-row`, `.it-row`, `.ir-row`, `.inbox-row` |
| **Dialog** | `size: sm (400) \| md (560) \| lg (760)`, `role: dialog \| alertdialog` | `Modal`, `ConfirmDialog`, `InviteModal`, `DoctorReportLinkDialog` |
| **Sheet** (bottom) | — | `BottomSheet` (уже есть) |
| **Drawer** (side) | `width: sm (392) \| md (480) \| lg (560)` | `SidePanel`, `.indicator-info-panel`, `.record-add-panel` ×2 |
| **Popover / Menu** | `min-width`, `placement` | `.action-menu-popover`, `.background-jobs-popover`, `.filters-popover`, `.app-search-dropdown`, `.app-family-switch-list` |
| **Toast** | `tone` (по общей семантике) | `Toast` (тона переназначить: success → ok) |
| **Alert / Banner** | `tone: info \| ok \| warning \| danger \| ai`, `icon`, `actions` | `.alert-danger`, `.alert-info`, `.ai-banner`, `.app-notice`, `WebSearchBanner`, `.settings-push-banner`, `.settings-menu-push-banner`, `.cf-warn`, `.rd-warn`, `.dr-hint-warn` |
| **Spinner** + **Skeleton** | `size: sm (12) \| md (18) \| lg`, `label` | `LoadingSpinner`, `.pipeline-step-spinner`, `.recognize-spinner`, `.step-spinner`, `record-batch-add-spin`, `ph-circle-notch`, 60 текстовых «Загрузка…» (на списках — Skeleton) |
| **EmptyState** | `icon`, `title`, `text`, `action`, `variant: block \| inline` | `.dr-empty`, `.dn-empty`, `.it-empty`, `.ic-empty`, `.records-empty`, `.chat-empty`, `.hh-tile-empty`, `p.muted` в `@empty` |
| **PageHeader** | `title` (h1), `kicker`, `actions`, `back` | `.dr-head`, `.dn-head`, `.ip-head`, `.vp-head`, `.hh-header`, `.home-header`, `.vx-header`, `.medkit-header`, `.record-add-header` ×2, `.settings-hero`, `.record-detail-topbar` |
| **Avatar** | `size: xs (24) \| sm (28) \| md (40) \| lg (48)` | `Avatar` (уже есть) + `.msg-avatar` (32), `.it-avatar` |
| **Text** (утилиты типографики) | `muted`, `caption`, `meta`, `kicker` | `.muted` + `.text-muted` (слить), `.card-meta`, `.card-kicker`, `.app-sidebar-kicker`, `.field-hint-muted`, локальные neutral-500/700 |

### Черновые шкалы (выведены из реально используемых значений)

**Палитра: семантические роли поверх существующих рамп.**

| Роль | Черновое значение | Откуда / что сливается |
|---|---|---|
| `bg` | #f3f2f2 | `--color-bg`, `neutral-100` (Δ5,7), `theme-color` |
| `surface` (хром: таб-бар, инпуты) | #eae9e9 | `--color-surface`, `neutral-200` (Δ2,8), `#e2e0df` |
| `paper` (карточки) | #fbfaf9 | `--color-paper` |
| `text` | #201e1d | `--color-text`, `neutral-900` в тенях |
| `text-muted` | #666262 | `neutral-600` + `neutral-700` (Δ9) + `opacity .8`; запретить `neutral-500` для текста |
| `text-subtle` (≥ 4,5:1) | ≈ text 62% | заменяет `text 50%`, `text 60%`, `neutral-500` |
| `border-subtle` / `border` / `border-strong` | text 8% / 12–16% / 38% | 3 уровня вместо 12 процентов `color-mix(text …)` и `divider` vs `control-border` |
| `hover-overlay` / `pressed-overlay` | text 6% / text 12% | 4, 5, 6, 7% → 6; 10, 12% → 12 |
| `accent` / `accent-hover` / `accent-pressed` / `accent-soft` | #006f94 / 600 / 800 / 100 | `accent`, `accent-600/700/800/100`; `accent-700` ≈ `accent` (Δ16) |
| `on-accent` / `on-status` | #fff (либо `bg`) | 23 `#fff` + `--color-bg` в той же роли |
| `danger` (одна!) | решить: `status-danger` #e03131 **или** `accent-2` #d6006c | сейчас две «опасности» (§4.10) |
| `ok` / `warning` / `danger` / `none` — каждая тройкой `fg` / `bg-soft` / `solid` | fg = `*-text`; soft = status 12% на paper; solid = status | 13 + 8 + 9 процентов подложки сводятся к одному `soft`; warning-fg сливается с `kind-wellbeing-text` и `#8a5a00` |
| `overlay-scrim` | neutral-900 55% | 55 и 65% → одно значение |
| `kind-*` (дневник) | оставить | `kind-wellbeing` #d97700 визуально близок к warning, стоит развести сильнее |

**Типографика** (px при 1rem = 17px; в коде хранить в rem):

| Токен | px | Сливает (текущие px) | Вхождений сейчас |
|---|---|---|---|
| `caption-xs` (только бейджи/счётчики) | 12 | 10.5, 11, 11.9, 12 | 16 |
| `caption` | 13 | 13, 13.5 | 156 |
| `meta` | 14 | 14, 14.5 | 88 |
| `body-sm` | 15 | 15, 15.3, 16, 16.1 (кроме `input`: остаётся `max(16px…)`) | ~65 |
| `body` | 17 | 16.5, 17, 17.5, 17.9, 18 | ~33 |
| `title-sm` (заголовок записи) | 19 | 18.7, 19, 19.5 | 18 |
| `title` | 21 | 20, 20.4, 21, 21.3, 22 | ~40 |
| `heading` | 25 | 23–26 | ~16 |
| `heading-lg` | 32 | 27–34 | ~10 |
| `display` | 42 | 37–42, 56 (спецслучай) | 8 |
| Начертания | 400 / 600 | 500 → 400, 700 → 600 (или загрузить 700) | — |
| line-height | `tight` 1.15 / `snug` 1.3 / `normal` 1.55 | 1.12–1.2 → tight; 1.25–1.45 → snug; 1.5–1.6 → normal; `1` только для иконок/бейджей | — |
| letter-spacing | `kicker` 0.1em / `heading` −0.015em | 0.08/0.1/0.12 → 0.1; −0.015/−0.02 → −0.015 | — |
| Моноширинный | один стек `ui-monospace, SFMono-Regular, Menlo, Consolas, monospace` | 4 разных стека | 7 |

**Отступы.** База 4px с микрошагами 2 и 6: чётные значения дают ~717 из 864 литералов, а восемь самых
частых (8, 10, 12, 4, 6, 16, 14, 2) — 651. Шкалу 5/10/15 из токенов стоит заменить, а не навязывать.

| Токен | px | Сливает | Вхождений сейчас |
|---|---|---|---|
| `space-0.5` | 2 | 1, 1.5, 2, 3 | ~76 |
| `space-1` | 4 | 4, 5 | ~112 |
| `space-1.5` | 6 | 6, 7 | ~89 |
| `space-2` | 8 | 8, 9 | ~148 |
| `space-2.5` | 10 | 10, 11 (+`--space-2`) | ~173 |
| `space-3` | 12 | 12, 13 | ~101 |
| `space-3.5` | 14 | 14, 15 (+`--space-3`) | ~92 |
| `space-4` | 16 | 16, 17, 18 | ~81 |
| `space-5` | 20 | 20, 22 (+`--space-4`) | ~47 |
| `space-6` | 24 | 24, 26, 28 | ~14 |
| `space-8` | 32 | 30, 32 (+`--space-6`) | ~6 |
| `space-10` | 40 | 40, 43 | ~10 |

(10 и 14 оставлены промежуточными шагами, потому что на них ~270 вхождений. Если редизайн
допускает сдвиг на ±2px, их можно отказаться и получить чистую шкалу 2/4/8/12/16/20/24/32/40.)

**Радиусы.** Сейчас концепция «острых углов» (1/2/4px), и это решение дизайна, а не случайность.
Черновик сохраняет её и убирает выбросы:

| Токен | Значение | Сливает |
|---|---|---|
| `radius-xs` | 2px | 1px (`sm`), 1,5px (`md*0.75`), 2px (`md`) — разница 1px неразличима |
| `radius-md` | 4px | `lg`, литералы 4px, fallback 4/6px |
| `radius-lg` | 12px | только «мягкие» поверхности: пузыри чата (12/4), sheet (если редизайн смягчит) |
| `radius-full` | 999px | `999px`, `50%` (аватары, точки, счётчики, пилюли) |

**Тени и фокус.** Оставить `shadow-sm/md/lg` (тени на `#2d2b2b` перевести на токены). Добавить
`focus-ring` (одно значение вместо 5) и `stripe` (полоса статуса: одна техника, 3 или 4px).

**z-index.** Оставить существующие 9 слоёв, добавить `--z-sticky: 10` (топбары) и `--z-dev` (вместо `9999`).

**Анимации.** `duration-fast` 120ms (0.05–0.15s), `duration-base` 200ms (0.18–0.25s), `duration-slow` 400ms,
`duration-spin` 700ms; `easing-standard: ease` / `easing-linear` для спиннеров.

**Брейкпоинты.** `bp-sm` 640, `bp-lg` 1024 (совпадают с `BreakpointService`). Выбросы 479/599/900/1100/1200
пересмотреть по месту; формы `max-width: 639px` и `640px` свести к одной.

**Размеры.** `tap-min` 44 (есть); `control-sm` 36; ширины Drawer 392/480/560 и Dialog 400/560/760;
`content-max` 760 / 1200.

---

## Как считалось (воспроизводимость)

Снимок кода: `git archive github/master src/FamilyHub.Web/src | tar -x -C <tmp>` (ветка в рабочей
копии в момент аудита отставала от master на 38 коммитов). Скрипты лежат в
[`docs/ui-audit-scripts/`](ui-audit-scripts/) (Node ≥ 18, без зависимостей). Запуск из корня репо:

```bash
SRC=src/FamilyHub.Web/src
node docs/ui-audit-scripts/components.mjs $SRC/app /tmp/components.json  # §2, §3.1–3.2: граф селекторов, роутинг, мёртвые
node docs/ui-audit-scripts/values.mjs     $SRC     /tmp/values.json      # §5: цвета, color-mix, шрифты, отступы, радиусы, тени, z, длительности, @media
node docs/ui-audit-scripts/classes.mjs    $SRC     /tmp/classes.json     # §3.3–3.4: глобальные классы, сырые <button>/<input>, локальные двойники
node docs/ui-audit-scripts/rules.mjs      $SRC tag status-chip dr-chip …  # §4: компактный дамп свойств правил по классам
node docs/ui-audit-scripts/tap.mjs        $SRC                            # §7: интерактивные правила с высотой/шириной < 40px
```

Что именно считают скрипты:

- **Декларации.** Все `*.scss`, первый блок `styles:` в каждом `*.ts`, атрибуты `style="…"` в шаблонах.
  Комментарии `//` и `/* */` вырезаются. Декларации внутри `:root {}` помечаются как определения токенов
  и в «использование» не входят.
- **Цвета.** hex, `rgb(a)`, `hsl(a)`, именованные цвета в свойствах `color|background|border|outline|shadow|fill|stroke|--*`;
  `color-mix(...)` считается целым выражением; отдельно ищутся hex/rgb в строковых литералах TS-логики.
- **Отступы.** Значения `margin*|padding*|gap|row-gap|column-gap` разбиваются на атомы (`8px 12px` → 2 атома).
- **Кегли.** rem переводятся в px умножением на 17 (база, от которой их переводили в v2.1).
- **Компоненты.** `@Component/@Directive` → селектор; вхождения считаются по `<selector` (или атрибуту) во всех
  шаблонах — внешних `.html` и `template:` в `.ts`. «Подключён к роутингу» = имя класса встречается как
  `m.ClassName` в `*routes.ts`.
- **Классы.** Учитываются статический `class="…"` и `[class.x]`. Классы, собранные строкой
  (`[class]="'avatar-' + size()"`, `ngClass`), **не учитываются**, поэтому `row-danger`/`row-cursor`
  с 0 вхождений стоит перепроверить вручную перед удалением.
- **Контраст.** Формула WCAG 2.x relative luminance; `color-mix` с прозрачностью раскладывается на фон, на котором лежит элемент.
- Точечные счётчики (`aria-*`, `@empty`, «Загрузка…», `:hover` и т.п.) получены `grep -rhoE … | wc -l`
  по `src/app` с `--include=*.html --include=*.ts` (и `*.scss` для псевдоклассов).

Ограничения: SCSS разбирается регулярными выражениями без компилятора (миксинов нет, `@extend` один:
`.visually-hidden-input`). Вложенные `color-mix` внутри `color-mix` считаются одним выражением.
Количество «вхождений» означает места в исходниках, а не число отрисованных элементов.
