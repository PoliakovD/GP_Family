import { Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, ApiError } from '../../services/api.service';
import { NotificationStateService } from '../../services/notification-state.service';
import { type AppNotification } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { notificationTypeIcon, notificationTypeLabel } from '../../shared/util/notification-type-labels';

const MONTHS_GEN = [
  'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
  'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря',
];

@Component({
  selector: 'app-notifications-tab',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent],
  templateUrl: './notifications-tab.component.html',
})
export class NotificationsTabComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notificationState = inject(NotificationStateService);

  items: AppNotification[] = [];
  unreadOnly = false;
  error: string | null = null;
  loading = true;

  ngOnInit(): void {
    this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading = true;
    try {
      this.items = await this.api.getNotifications(this.unreadOnly);
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить оповещения.';
    } finally {
      this.loading = false;
    }
  }

  async handleMarkRead(id: string): Promise<void> {
    try {
      await this.api.markNotificationRead(id);
      this.notificationState.decrementLocal();
      await this.refresh();
    } catch (err) {
      this.error =
        err instanceof ApiError ? err.message : 'Не удалось отметить как прочитанное.';
    }
  }

  /** Бэкенд не отдаёт bulk-эндпоинт — отмечаем каждое непрочитанное существующим методом. */
  async markAllRead(): Promise<void> {
    const unread = this.items.filter((n) => !n.isRead);
    if (unread.length === 0) return;
    try {
      await Promise.all(unread.map((n) => this.api.markNotificationRead(n.id)));
      await this.notificationState.refresh();
      await this.refresh();
    } catch (err) {
      this.error =
        err instanceof ApiError ? err.message : 'Не удалось отметить все как прочитанные.';
    }
  }

  typeLabel(type: number): string {
    return notificationTypeLabel(type);
  }

  typeIcon(type: number): string {
    return notificationTypeIcon(type);
  }

  /** "Сегодня" / "Вчера" / "18 июля" — кикер группы даты (см. дизайн-дэк). */
  private dateKicker(dateStr: string): string {
    const d = new Date(dateStr);
    const day = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    const today = new Date();
    const todayDay = new Date(today.getFullYear(), today.getMonth(), today.getDate());
    const diffDays = Math.round((todayDay.getTime() - day.getTime()) / 86_400_000);
    if (diffDays === 0) return 'Сегодня';
    if (diffDays === 1) return 'Вчера';
    return `${day.getDate()} ${MONTHS_GEN[day.getMonth()]}`;
  }

  /** Группирует уже отсортированный бэкендом список по соседним записям одной даты. */
  get groupedByDate(): { kicker: string; items: AppNotification[] }[] {
    const groups: { kicker: string; items: AppNotification[] }[] = [];
    for (const n of this.items) {
      const kicker = this.dateKicker(n.createdAt);
      const last = groups[groups.length - 1];
      if (last && last.kicker === kicker) last.items.push(n);
      else groups.push({ kicker, items: [n] });
    }
    return groups;
  }
}
