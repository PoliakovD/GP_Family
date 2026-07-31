import { Routes } from '@angular/router';
import { authGuard, consentGuard } from './services/auth.guards';
import { pendingCodeGuard } from './services/pending-code.guard';

export const routes: Routes = [
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
      { path: '', redirectTo: 'profile', pathMatch: 'full' },
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
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/home/home.component').then((m) => m.HomeComponent),
  },
  // Обратная совместимость со старой прямой ссылкой на отдельную страницу поиска — теперь
  // поиск живёт на Главной (см. HomeComponent).
  { path: 'search', redirectTo: 'home' },
  {
    path: 'families',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/families-tab/families-tab.component').then(
        (m) => m.FamiliesTabComponent,
      ),
  },
  {
    path: 'families/:id',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/family-details/family-details.component').then(
        (m) => m.FamilyDetailsComponent,
      ),
  },
  {
    // Хаб «Здоровье» (редизайн навигации): Аптечка + Анализы под одним табом, настоящие
    // вложенные роуты (не in-page state) — переживают refresh, работают с browser back.
    path: 'health',
    canActivate: [authGuard, consentGuard],
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
        // Общий обезличенный справочник препаратов (этап 4) — наполняется AI-конвейером обогащения.
        path: 'kb',
        loadComponent: () =>
          import('./components/kb-tab/kb-tab.component').then((m) => m.KbTabComponent),
      },
    ],
  },
  // Обратная совместимость со старыми прямыми ссылками/букмарками на плоские роуты.
  { path: 'medications', redirectTo: 'health/medications' },
  { path: 'records', redirectTo: 'health/records' },
  {
    path: 'birthdays',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/birthdays-tab/birthdays-tab.component').then(
        (m) => m.BirthdaysTabComponent,
      ),
  },
  {
    path: 'notifications',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/notifications-tab/notifications-tab.component').then(
        (m) => m.NotificationsTabComponent,
      ),
  },
  { path: '**', redirectTo: 'home' },
];
