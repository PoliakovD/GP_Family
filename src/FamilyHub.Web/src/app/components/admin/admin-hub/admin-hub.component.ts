import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AdminApiService } from '../../../services/admin-api.service';

/**
 * Хаб админ-панели (ADR-0009): шесть разделов верхнего уровня, внутри каждого (кроме «Требует
 * внимания») — вложенные страницы, см. admin.routes.ts. Тот же паттерн вложенных роутов, что
 * SettingsComponent/HealthHubComponent (patterns/frontend_web.md, «Хаб-паттерн»).
 *
 * Разделы сгруппированы по тому, ЧТО админ делает, а не по тому, из какой подсистемы пришли:
 *  - Требует внимания — что сломано прямо сейчас;
 *  - Мониторинг — как чувствует себя система (только чтение);
 *  - Безопасность — ключи, ротация, статистика доступа;
 *  - Настройки — то, что можно менять без передеплоя;
 *  - Операции — разовые и рабочие действия (задачи, прогрев, пересборки);
 *  - Справочник — ручная правка справочников после ИИ.
 */
@Component({
    selector: 'app-admin-hub',
    imports: [RouterLink, RouterLinkActive, RouterOutlet],
    templateUrl: './admin-hub.component.html',
    styleUrl: './admin-hub.component.scss'
})
export class AdminHubComponent {
  private readonly api = inject(AdminApiService);
  private readonly router = inject(Router);

  readonly sections: { path: string; label: string }[] = [
    { path: 'attention', label: 'Требует внимания' },
    { path: 'monitoring', label: 'Мониторинг' },
    { path: 'security', label: 'Безопасность' },
    { path: 'settings', label: 'Настройки' },
    { path: 'operations', label: 'Операции' },
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
