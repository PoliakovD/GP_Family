import { Routes } from '@angular/router';
import { authGuard, consentGuard, profileGuard } from './services/auth.guards';
import { adminGuard } from './services/admin.guards';
import { pendingCodeGuard } from './services/pending-code.guard';

export const routes: Routes = [
  // Админ-панель (ADR-0009) — полностью отдельная от остального приложения поверхность:
  // своя сессия (adminGuard, cookie familyhub.admin), не участвует в authGuard/consentGuard.
  // В проде публичный домен блокирует /admin* на уровне Caddy (защита в глубину) — доступен
  // только с admin.{PUBLIC_DOMAIN}, но роут остаётся частью общего SPA-бандла (см. деплой-план).
  {
    path: 'admin/login',
    loadComponent: () =>
      import('./components/admin/admin-login/admin-login.component').then((m) => m.AdminLoginComponent),
  },
  {
    path: 'admin',
    canActivate: [adminGuard],
    loadComponent: () =>
      import('./components/admin/admin-hub/admin-hub.component').then((m) => m.AdminHubComponent),
    // Дочерние роуты (шесть разделов, вложенные страницы, редиректы со старых адресов) — в
    // отдельном файле: их много, а грузятся они только при заходе в админку.
    loadChildren: () => import('./components/admin/admin.routes').then((m) => m.ADMIN_ROUTES),
  },

  // Публичные / служебные маршруты (без гардов).
  {
    path: 'login',
    title: 'Вход',
    canDeactivate: [pendingCodeGuard],
    loadComponent: () =>
      import('./components/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // Первичная привязка Telegram Mini App к email-аккаунту (см. authGuard/TelegramBindComponent).
    path: 'telegram-bind',
    title: 'Подтверждение почты',
    canDeactivate: [pendingCodeGuard],
    loadComponent: () =>
      import('./components/telegram-bind/telegram-bind.component').then((m) => m.TelegramBindComponent),
  },
  {
    // Публичный лендинг приглашения (веб-альтернатива Telegram-инвайту, см. FamilyDetailsComponent) —
    // намеренно БЕЗ гардов: гость должен увидеть превью и решить, создавать ли аккаунт, до входа.
    path: 'join/:code',
    title: 'Приглашение в семью',
    loadComponent: () =>
      import('./components/join-invite/join-invite.component').then((m) => m.JoinInviteComponent),
  },
  {
    // Страница врача по публичной ссылке на отчёт — намеренно БЕЗ гардов и без оболочки приложения:
    // смотрит человек без аккаунта (доступ — токен в пути, см. DoctorReportEndpoints).
    path: 'r/:token',
    title: 'Отчёт для врача',
    loadComponent: () =>
      import('./components/public-report/public-report.component').then((m) => m.PublicReportComponent),
  },
  {
    // Сбор ФИО/ДР/пола (identity rework) — единственный путь сюда: profileGuard на данных
    // роутах ниже, куда попадает свежепривязанный Telegram-аккаунт без профиля. authGuard, а не
    // profileGuard/consentGuard — экран сам и есть цель редиректа, требует только вход.
    path: 'profile-setup',
    title: 'Заполните профиль',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/profile-setup/profile-setup.component').then((m) => m.ProfileSetupComponent),
  },
  {
    path: 'privacy',
    title: 'Политика конфиденциальности',
    loadComponent: () =>
      import('./components/privacy/privacy.component').then((m) => m.PrivacyComponent),
  },
  {
    // Текст согласия ПДн, доступный БЕЗ входа (в отличие от /consent ниже — это гейт
    // ПРИНЯТИЯ для уже аутентифицированного пользователя). Сюда ведут ссылки с формы
    // регистрации, где согласие нужно прочитать до создания аккаунта.
    path: 'consent-text',
    loadComponent: () =>
      import('./components/consent-text/consent-text.component').then((m) => m.ConsentTextComponent),
  },
  {
    path: 'consent',
    title: 'Согласие на обработку данных',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/consent-gate/consent-gate.component').then((m) => m.ConsentGateComponent),
  },
  {
    // Хаб «Настройки» (вкладки Профиль/Безопасность/Уведомления/Данные) — намеренно БЕЗ
    // consentGuard, в отличие от блока данных ниже: настройки должны быть доступны и до
    // принятия согласия ПДн (например, чтобы выйти или посмотреть политику конфиденциальности).
    path: 'settings',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/settings/settings.component').then((m) => m.SettingsComponent),
    children: [
      {
        // Редизайн v3, PR8 — на десктопе редиректит на 'profile' (как раньше), на мобильном
        // (заход через нижний лист «Ещё» → «Профиль») показывает корневой список разделов с
        // шевронами вместо мгновенного редиректа, см. settings-menu.component.ts.
        path: '',
        title: 'Профиль',
        loadComponent: () =>
          import('./components/settings/settings-menu/settings-menu.component').then(
            (m) => m.SettingsMenuComponent,
          ),
      },
      {
        path: 'profile',
        title: 'Аккаунт',
        loadComponent: () =>
          import('./components/settings/profile/settings-profile.component').then(
            (m) => m.SettingsProfileComponent,
          ),
      },
      {
        path: 'security',
        title: 'Безопасность',
        loadComponent: () =>
          import('./components/settings/security/settings-security.component').then(
            (m) => m.SettingsSecurityComponent,
          ),
      },
      {
        path: 'notifications',
        title: 'Уведомления',
        loadComponent: () =>
          import('./components/settings/notifications/settings-notifications.component').then(
            (m) => m.SettingsNotificationsComponent,
          ),
      },
      {
        path: 'data',
        title: 'Данные и доступ',
        loadComponent: () =>
          import('./components/settings/data/settings-data.component').then(
            (m) => m.SettingsDataComponent,
          ),
      },
    ],
  },

  // Данные: требуют входа (PWA) и принятого согласия ПДн (задачи 2.3/2.4).
  { path: '', redirectTo: 'home', pathMatch: 'full' },
  {
    // Главная (редизайн навигации): глобальный поиск + вход в «Семьи» + виджет дней рождения.
    path: 'home',
    title: 'Главная',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/home/home.component').then((m) => m.HomeComponent),
  },
  // Обратная совместимость со старой прямой ссылкой на отдельную страницу поиска — теперь
  // поиск живёт на Главной (см. HomeComponent).
  { path: 'search', redirectTo: 'home' },
  {
    path: 'families',
    title: 'Семьи',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/families-tab/families-tab.component').then(
        (m) => m.FamiliesTabComponent,
      ),
  },
  {
    path: 'families/:id',
    title: 'Семья',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/family-details/family-details.component').then(
        (m) => m.FamilyDetailsComponent,
      ),
  },
  {
    // Хаб «Здоровье» (редизайн навигации): Аптечка + Анализы под одним табом, настоящие
    // вложенные роуты (не in-page state) — переживают refresh, работают с browser back.
    path: 'health',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/health-hub/health-hub.component').then((m) => m.HealthHubComponent),
    children: [
      {
        // Хаб «Здоровье» (редизайн навигации, макет «Screen - Health hub») — плитки трёх групп
        // со сводкой своих данных, заменил редирект на «Аптечку».
        path: '',
        title: 'Моё здоровье',
        loadComponent: () =>
          import('./components/health-home/health-home.component').then((m) => m.HealthHomeComponent),
      },
      {
        path: 'medications',
        title: 'Аптечка',
        loadComponent: () =>
          import('./components/medications-tab/medications-tab.component').then(
            (m) => m.MedicationsTabComponent,
          ),
      },
      {
        // Экран открытой аптечки (редизайн v2.2) — та же пара, что records/records/:id: сосед
        // 'medications', не потомок medications-tab (health-hub монтирует оба через свой
        // router-outlet, см. health-hub.component.html). 'medications/new' сегодня не
        // существует — конфликта порядка регистрации нет (см. комментарий у records/new ниже).
        path: 'medications/:id',
        title: 'Аптечка',
        loadComponent: () =>
          import('./components/medkit-detail-page/medkit-detail-page.component').then(
            (m) => m.MedkitDetailPageComponent,
          ),
      },
      {
        // Приём лекарств (ADR-0015): «Сегодня» и «Курсы» — вложенные роуты страницы-хаба, форма курса и
        // настройки напоминаний — общие панели хаба. dose/:doseId — цель клика по push-уведомлению.
        path: 'intake',
        title: 'Приём лекарств',
        loadComponent: () =>
          import('./components/intake-page/intake-page.component').then((m) => m.IntakePageComponent),
        children: [
          {
            path: '',
            loadComponent: () =>
              import('./components/intake-today/intake-today.component').then((m) => m.IntakeTodayComponent),
          },
          {
            path: 'dose/:doseId',
            loadComponent: () =>
              import('./components/intake-today/intake-today.component').then((m) => m.IntakeTodayComponent),
          },
          {
            path: 'courses',
            loadComponent: () =>
              import('./components/intake-courses/intake-courses.component').then((m) => m.IntakeCoursesComponent),
          },
          {
            path: 'courses/:id',
            loadComponent: () =>
              import('./components/intake-courses/intake-courses.component').then((m) => m.IntakeCoursesComponent),
          },
        ],
      },
      {
        path: 'records',
        title: 'Анализы',
        loadComponent: () =>
          import('./components/medical-records-tab/medical-records-tab.component').then(
            (m) => m.MedicalRecordsTabComponent,
          ),
      },
      {
        // Экран добавления (редизайн v3, PR7) — боковая панель на десктопе, полноэкранно на
        // мобильном, см. record-add-page.component.ts. ДО 'records/:id' — иначе роутер принял
        // бы литеральный сегмент 'new' за :id (порядок регистрации важен для Angular Router,
        // в отличие от ASP.NET Core Minimal API, где специфичность важнее порядка).
        path: 'records/new',
        title: 'Новый анализ',
        loadComponent: () =>
          import('./components/record-add-page/record-add-page.component').then(
            (m) => m.RecordAddPageComponent,
          ),
      },
      {
        // Батч-загрузка (кнопка «+ Несколько» рядом с «+ Добавить», см. medical-records-panel) —
        // ДО 'records/:id' по той же причине, что и 'records/new' выше (литеральный сегмент
        // должен победить :id).
        path: 'records/batch',
        title: 'Загрузка анализов',
        loadComponent: () =>
          import('./components/record-batch-add-page/record-batch-add-page.component').then(
            (m) => m.RecordBatchAddPageComponent,
          ),
      },
      {
        // Мобильный экран открытой записи (редизайн v3, PR6) — деслктоп продолжает раскрывать
        // запись инлайн в списке, см. record-detail-page.component.ts.
        path: 'records/:id',
        title: 'Анализ',
        loadComponent: () =>
          import('./components/record-detail-page/record-detail-page.component').then(
            (m) => m.RecordDetailPageComponent,
          ),
      },
      {
        path: 'visits',
        title: 'Приёмы врача',
        loadComponent: () =>
          import('./components/doctor-visits-tab/doctor-visits-tab.component').then(
            (m) => m.DoctorVisitsTabComponent,
          ),
      },
      {
        path: 'visits/new',
        title: 'Новый приём врача',
        loadComponent: () =>
          import('./components/doctor-visit-add/doctor-visit-add.component').then(
            (m) => m.DoctorVisitAddComponent,
          ),
      },
      {
        // Батч-загрузка для «Врачи» — та же причина порядка, что records/batch выше.
        path: 'visits/batch',
        title: 'Загрузка приёмов врача',
        loadComponent: () =>
          import('./components/doctor-visit-batch-add/doctor-visit-batch-add.component').then(
            (m) => m.DoctorVisitBatchAddComponent,
          ),
      },
      {
        path: 'visits/:id',
        title: 'Приём врача',
        loadComponent: () =>
          import('./components/doctor-visit-detail-page/doctor-visit-detail-page.component').then(
            (m) => m.DoctorVisitDetailPageComponent,
          ),
      },
      {
        // Прививки (ADR-0016): «Стоит запланировать» + семья в обзоре, график человека — вложенные
        // роуты страницы-хаба (та же схема, что intake). people/:kind/:id/series/:code открывает
        // деталь серии — на десктопе справа (сплит, как intake-courses/:id), на мобиле отдельным
        // экраном (тот же компонент, id/code — опциональные component-bound входы маршрута).
        path: 'vaccinations',
        title: 'Прививки',
        loadComponent: () =>
          import('./components/vaccinations-page/vaccinations-page.component').then(
            (m) => m.VaccinationsPageComponent,
          ),
        children: [
          {
            path: '',
            loadComponent: () =>
              import('./components/vaccinations-overview/vaccinations-overview.component').then(
                (m) => m.VaccinationsOverviewComponent,
              ),
          },
          {
            path: 'people/:kind/:id',
            loadComponent: () =>
              import('./components/vaccination-person/vaccination-person.component').then(
                (m) => m.VaccinationPersonComponent,
              ),
          },
          {
            path: 'people/:kind/:id/series/:code',
            loadComponent: () =>
              import('./components/vaccination-person/vaccination-person.component').then(
                (m) => m.VaccinationPersonComponent,
              ),
          },
        ],
      },
      {
        // Личный дневник самочувствия — замеры, симптомы, самочувствие, лекарства, сон, заметки.
        path: 'notes',
        title: 'Дневник',
        loadComponent: () =>
          import('./components/health-notes-tab/health-notes-tab.component').then(
            (m) => m.HealthNotesTabComponent,
          ),
      },
      {
        // Отчёты для врача — PDF-снимок данных пациента + публичная ссылка. ?new=1&diary=1 открывает
        // создание сразу (кнопка «В отчёт для врача» в дневнике).
        path: 'reports',
        title: 'Отчёты для врача',
        loadComponent: () =>
          import('./components/doctor-reports-tab/doctor-reports-tab.component').then(
            (m) => m.DoctorReportsTabComponent,
          ),
      },
      {
        // Мини-хаб «Справочник» (редизайн v2, PR4) — тот же паттерн вложенности, что у health-hub
        // самого. medications — прежний KbTabComponent без изменений содержимого, просто
        // перемонтирован под дочерний роут; indicators — новый справочник показателей.
        path: 'kb',
        title: 'Справочник',
        loadComponent: () =>
          import('./components/kb-hub/kb-hub.component').then((m) => m.KbHubComponent),
        children: [
          { path: '', redirectTo: 'medications', pathMatch: 'full' },
          {
            path: 'medications',
            loadComponent: () =>
              import('./components/kb-tab/kb-tab.component').then((m) => m.KbTabComponent),
          },
          {
            path: 'indicators',
            loadComponent: () =>
              import('./components/kb-analyte-tab/kb-analyte-tab.component').then(
                (m) => m.KbAnalyteTabComponent,
              ),
          },
          {
            // Статические карточки каталога прививок (ADR-0016) — без ИИ-обогащения, в отличие от
            // соседних вкладок; ?id= открывает конкретную серию, тот же приём, что kb-analyte-tab.
            path: 'vaccines',
            loadComponent: () =>
              import('./components/kb-vaccines-tab/kb-vaccines-tab.component').then(
                (m) => m.KbVaccinesTabComponent,
              ),
          },
        ],
      },
      {
        // Ветка medicalrecords (задачи 5.2/5.3): «мои показатели» — последнее значение по каждому
        // распознанному лабораторному показателю, история со спарклайном по клику.
        path: 'indicators',
        title: 'Показатели',
        loadComponent: () =>
          import('./components/indicators-tab/indicators-tab.component').then(
            (m) => m.IndicatorsTabComponent,
          ),
      },
    ],
  },
  // Обратная совместимость со старыми прямыми ссылками/букмарками на плоские роуты.
  { path: 'medications', redirectTo: 'health/medications' },
  { path: 'records', redirectTo: 'health/records' },
  {
    path: 'birthdays',
    title: 'Дни рождения',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/birthdays-tab/birthdays-tab.component').then(
        (m) => m.BirthdaysTabComponent,
      ),
  },
  {
    path: 'notifications',
    title: 'Уведомления',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/notifications-tab/notifications-tab.component').then(
        (m) => m.NotificationsTabComponent,
      ),
  },
  // Раньше любой неверный адрес молча уводил на Главную — человек не понимал, почему ссылка «не та».
  {
    path: '**',
    title: 'Страница не найдена',
    loadComponent: () => import('./components/not-found/not-found.component').then((m) => m.NotFoundComponent),
  },
];
