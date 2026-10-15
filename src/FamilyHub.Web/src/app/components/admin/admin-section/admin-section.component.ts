import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
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
    imports: [RouterLink, RouterLinkActive, RouterOutlet],
    changeDetection: ChangeDetectionStrategy.Eager,
    template: `
    <nav class="seg seg-sunken admin-tabs" aria-label="Страницы раздела">
      @for (t of tabs; track t.path) {
        <a class="seg-opt" [routerLink]="[t.path]" routerLinkActive="active" ariaCurrentWhenActive="page">{{ t.label }}</a>
      }
    </nav>

    <router-outlet></router-outlet>
  `,
  styles: [`
    :host { display: flex; flex-direction: column; gap: var(--space-4); }
    .admin-tabs { align-self: flex-start; max-width: 100%; }
  `],
})
export class AdminSectionComponent {
  readonly tabs: AdminTab[] = inject(ActivatedRoute).snapshot.data['tabs'] ?? [];
}
