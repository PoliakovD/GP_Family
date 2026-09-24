import { Component, inject } from '@angular/core';
import { ActivatedRoute, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

export interface AdminTab {
  path: string;
  label: string;
}

/**
 * Раздел админки с вложенными страницами: второй уровень переключателя (`.seg`) + `router-outlet`.
 * Список вкладок приходит из `route.data.tabs` (см. admin.routes.ts) — один компонент на все
 * разделы вместо пяти почти одинаковых. Вкладки — настоящие дочерние роуты (переживают F5, работают
 * с «назад»), тот же хаб-паттерн, что SettingsComponent/HealthHubComponent (patterns/frontend_web.md).
 */
@Component({
  selector: 'app-admin-section',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="seg mb-3">
      @for (t of tabs; track t.path) {
        <a class="seg-opt" [routerLink]="[t.path]" routerLinkActive="active">{{ t.label }}</a>
      }
    </div>

    <router-outlet></router-outlet>
  `,
})
export class AdminSectionComponent {
  readonly tabs: AdminTab[] = inject(ActivatedRoute).snapshot.data['tabs'] ?? [];
}
