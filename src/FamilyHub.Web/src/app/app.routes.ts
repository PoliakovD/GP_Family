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
    path: 'medications',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/medications-tab/medications-tab.component').then(
        (m) => m.MedicationsTabComponent,
      ),
  },
  {
    path: 'birthdays',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/birthdays-tab/birthdays-tab.component').then(
        (m) => m.BirthdaysTabComponent,
      ),
  },
  {
    path: 'records',
    canActivate: [authGuard, consentGuard],
    loadComponent: () =>
      import('./components/medical-records-tab/medical-records-tab.component').then(
        (m) => m.MedicalRecordsTabComponent,
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
