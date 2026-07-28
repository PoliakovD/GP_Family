import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * Хаб «Настройки» (редизайн навигации): вкладки Профиль/Безопасность/Уведомления/Данные вместо
 * одного длинного скролла. Настоящие вложенные роуты (не in-page state) — переживают refresh и
 * работают с browser back, тот же паттерн, что и HealthHubComponent (/health). Дочерние компоненты
 * монтируются в router-outlet этого хаба, а не корневого.
 */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  readonly sections: { path: string; label: string }[] = [
    { path: 'profile', label: 'Профиль' },
    { path: 'security', label: 'Безопасность' },
    { path: 'notifications', label: 'Уведомления' },
    { path: 'data', label: 'Данные' },
  ];
}
