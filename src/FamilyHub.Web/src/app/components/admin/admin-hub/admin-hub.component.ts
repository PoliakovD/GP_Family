import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AdminApiService } from '../../../services/admin-api.service';

/**
 * Хаб админ-панели (ADR-0009): вкладки Обзор/Хранилище/Система/Ключи — тот же паттерн
 * вложенных роутов, что SettingsComponent/HealthHubComponent (patterns/frontend_web.md,
 * «Хаб-паттерн — второе применение подтвердило, что это правило»).
 */
@Component({
  selector: 'app-admin-hub',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './admin-hub.component.html',
  styleUrl: './admin-hub.component.scss',
})
export class AdminHubComponent {
  private readonly api = inject(AdminApiService);
  private readonly router = inject(Router);

  readonly sections: { path: string; label: string }[] = [
    { path: 'overview', label: 'Обзор' },
    { path: 'storage', label: 'Хранилище' },
    { path: 'system', label: 'Система' },
    { path: 'keys', label: 'Ключи' },
    { path: 'enrichment', label: 'Обогащение' },
    { path: 'pipeline', label: 'Пайплайн' },
    { path: 'catalog', label: 'Справочник' },
  ];

  async logout(): Promise<void> {
    try {
      await this.api.logout();
    } finally {
      await this.router.navigate(['/admin/login']);
    }
  }
}
