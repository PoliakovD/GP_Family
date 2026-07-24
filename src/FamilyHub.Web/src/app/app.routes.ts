import { Routes } from '@angular/router';
import { authGuard, consentGuard } from './services/auth.guards';

export const routes: Routes = [
  // Публичные / служебные маршруты (без гардов).
  {
    path: 'login',
    loadComponent: () =>
      import('./components/login/login.component').then((m) => m.LoginComponent),
  },
  {
    path: 'privacy',
    loadComponent: () =>
      import('./components/privacy/privacy.component').then((m) => m.PrivacyComponent),
  },
  {
    path: 'consent',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/consent-gate/consent-gate.component').then((m) => m.ConsentGateComponent),
  },
  {
    path: 'settings',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/settings/settings.component').then((m) => m.SettingsComponent),
  },

  // Данные: требуют входа (PWA) и принятого согласия ПДн (задачи 2.3/2.4).
  { path: '', redirectTo: 'families', pathMatch: 'full' },
  {
    path: 'search',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/search/search.component').then((m) => m.SearchComponent),
  },
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
  { path: '**', redirectTo: 'families' },
];
