import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'families', pathMatch: 'full' },
  {
    path: 'families',
    loadComponent: () =>
      import('./components/families-tab/families-tab.component').then(
        (m) => m.FamiliesTabComponent,
      ),
  },
  {
    path: 'families/:id',
    loadComponent: () =>
      import('./components/family-details/family-details.component').then(
        (m) => m.FamilyDetailsComponent,
      ),
  },
  {
    path: 'medications',
    loadComponent: () =>
      import('./components/medications-tab/medications-tab.component').then(
        (m) => m.MedicationsTabComponent,
      ),
  },
  {
    path: 'birthdays',
    loadComponent: () =>
      import('./components/birthdays-tab/birthdays-tab.component').then(
        (m) => m.BirthdaysTabComponent,
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
    path: 'notifications',
    loadComponent: () =>
      import('./components/notifications-tab/notifications-tab.component').then(
        (m) => m.NotificationsTabComponent,
      ),
  },
  { path: '**', redirectTo: 'families' },
];
