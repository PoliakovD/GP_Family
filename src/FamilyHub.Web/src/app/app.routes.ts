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
    children: [
      { path: '', redirectTo: 'overview', pathMatch: 'full' },
      {
        path: 'overview',
        loadComponent: () =>
          import('./components/admin/admin-overview/admin-overview.component').then(
            (m) => m.AdminOverviewComponent,
          ),
      },
      {
        path: 'storage',
        loadComponent: () =>
          import('./components/admin/admin-storage/admin-storage.component').then(
            (m) => m.AdminStorageComponent,
          ),
      },
      {
        path: 'system',
        loadComponent: () =>
          import('./components/admin/admin-system/admin-system.component').then(
            (m) => m.AdminSystemComponent,
          ),
      },
      {
        path: 'keys',
        loadComponent: () =>
          import('./components/admin/admin-keys/admin-keys.component').then(
            (m) => m.AdminKeysComponent,
          ),
      },
      {
        // Пересборка enrich-пайплайна: доверенные домены + кэш сырых результатов поиска.
        path: 'enrichment',
        loadComponent: () =>
          import('./components/admin/admin-enrichment/admin-enrichment.component').then(
            (m) => m.AdminEnrichmentComponent,
          ),
      },
    ],
  },

  // Публичные / служебные маршруты (без гардов).
  {
    path: 'login',
    canDeactivate: [pendingCodeGuard],
    loadComponent: () =>
      import('./components/login/login.component').then((m) => m.LoginComponent),
  },
  {
    // Первичная привязка Telegram Mini App к email-аккаунту (см. authGuard/TelegramBindComponent).
    path: 'telegram-bind',
    canDeactivate: [pendingCodeGuard],
    loadComponent: () =>
      import('./components/telegram-bind/telegram-bind.component').then((m) => m.TelegramBindComponent),
  },
  {
    // Публичный лендинг приглашения (веб-альтернатива Telegram-инвайту, см. FamilyDetailsComponent) —
    // намеренно БЕЗ гардов: гость должен увидеть превью и решить, создавать ли аккаунт, до входа.
    path: 'join/:code',
    loadComponent: () =>
      import('./components/join-invite/join-invite.component').then((m) => m.JoinInviteComponent),
  },
  {
    // Сбор ФИО/ДР/пола (identity rework) — единственный путь сюда: profileGuard на данных
    // роутах ниже, куда попадает свежепривязанный Telegram-аккаунт без профиля. authGuard, а не
    // profileGuard/consentGuard — экран сам и есть цель редиректа, требует только вход.
    path: 'profile-setup',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/profile-setup/profile-setup.component').then((m) => m.ProfileSetupComponent),
  },
  {
    path: 'privacy',
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
        loadComponent: () =>
          import('./components/settings/settings-menu/settings-menu.component').then(
            (m) => m.SettingsMenuComponent,
          ),
      },
      {
        path: 'profile',
        loadComponent: () =>
          import('./components/settings/profile/settings-profile.component').then(
            (m) => m.SettingsProfileComponent,
          ),
      },
      {
        path: 'security',
        loadComponent: () =>
          import('./components/settings/security/settings-security.component').then(
            (m) => m.SettingsSecurityComponent,
          ),
      },
      {
        path: 'notifications',
        loadComponent: () =>
          import('./components/settings/notifications/settings-notifications.component').then(
            (m) => m.SettingsNotificationsComponent,
          ),
      },
      {
        path: 'data',
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
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/home/home.component').then((m) => m.HomeComponent),
  },
  // Обратная совместимость со старой прямой ссылкой на отдельную страницу поиска — теперь
  // поиск живёт на Главной (см. HomeComponent).
  { path: 'search', redirectTo: 'home' },
  {
    path: 'families',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/families-tab/families-tab.component').then(
        (m) => m.FamiliesTabComponent,
      ),
  },
  {
    path: 'families/:id',
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
      { path: '', redirectTo: 'medications', pathMatch: 'full' },
      {
        path: 'medications',
        loadComponent: () =>
          import('./components/medications-tab/medications-tab.component').then(
            (m) => m.MedicationsTabComponent,
          ),
      },
      {
        path: 'records',
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
        loadComponent: () =>
          import('./components/record-add-page/record-add-page.component').then(
            (m) => m.RecordAddPageComponent,
          ),
      },
      {
        // Мобильный экран открытой записи (редизайн v3, PR6) — деслктоп продолжает раскрывать
        // запись инлайн в списке, см. record-detail-page.component.ts.
        path: 'records/:id',
        loadComponent: () =>
          import('./components/record-detail-page/record-detail-page.component').then(
            (m) => m.RecordDetailPageComponent,
          ),
      },
      {
        path: 'visits',
        loadComponent: () =>
          import('./components/doctor-visits-tab/doctor-visits-tab.component').then(
            (m) => m.DoctorVisitsTabComponent,
          ),
      },
      {
        path: 'visits/new',
        loadComponent: () =>
          import('./components/doctor-visit-add/doctor-visit-add.component').then(
            (m) => m.DoctorVisitAddComponent,
          ),
      },
      {
        path: 'visits/:id',
        loadComponent: () =>
          import('./components/doctor-visit-detail-page/doctor-visit-detail-page.component').then(
            (m) => m.DoctorVisitDetailPageComponent,
          ),
      },
      {
        // Мини-хаб «Справочник» (редизайн v2, PR4) — тот же паттерн вложенности, что у health-hub
        // самого. medications — прежний KbTabComponent без изменений содержимого, просто
        // перемонтирован под дочерний роут; indicators — новый справочник показателей.
        path: 'kb',
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
        ],
      },
      {
        // Ветка medicalrecords (задачи 5.2/5.3): «мои показатели» — последнее значение по каждому
        // распознанному лабораторному показателю, история со спарклайном по клику.
        path: 'indicators',
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
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/birthdays-tab/birthdays-tab.component').then(
        (m) => m.BirthdaysTabComponent,
      ),
  },
  {
    path: 'notifications',
    canActivate: [authGuard, consentGuard, profileGuard],
    loadComponent: () =>
      import('./components/notifications-tab/notifications-tab.component').then(
        (m) => m.NotificationsTabComponent,
      ),
  },
  { path: '**', redirectTo: 'home' },
];
