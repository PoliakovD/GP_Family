import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Bell } from 'lucide-angular';
import { IconComponent } from '../ui/icon/icon.component';
import { NotificationStateService } from '../../services/notification-state.service';

/**
 * Колокольчик уведомлений — редизайн навигации (макет «Здоровье хаб»): «Уведомления» ушли из
 * бокового меню и мобильного листа «Ещё» (оба упразднены) сюда, в верхнюю строку каркаса, рядом с
 * аватаром на десктопе и в мобильной шапке — как в привычных приложениях. Один компонент на обе
 * ширины, а не два места разметки.
 */
@Component({
  selector: 'app-notification-bell',
  standalone: true,
  imports: [RouterLink, IconComponent],
  template: `
    <a routerLink="/notifications" class="btn btn-icon notification-bell" aria-label="Уведомления" title="Уведомления">
      <app-icon [icon]="bell" />
      @if (notifications.unread() > 0) {
        <span class="count-badge notification-bell-badge">{{ notifications.unread() }}</span>
      }
    </a>
  `,
  styleUrl: './notification-bell.component.scss',
})
export class NotificationBellComponent {
  readonly notifications = inject(NotificationStateService);
  protected readonly bell = Bell;
}
