import { Component, OnInit, inject, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { ClickableDirective } from '../../shared/util/clickable.directive';
import { NotificationStateService } from '../../services/notification-state.service';
import { type AppNotification } from '../../models/types';
import { relatedEntityQueryParams, relatedEntityRoute } from '../../shared/util/related-kind-route';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { notificationTypeIcon, notificationTypeLabel, notificationTypeTone } from '../../shared/util/notification-type-labels';
import { Bell, CheckCheck, ChevronRight } from 'lucide';
import type { IconNode as LucideIconData } from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { IconTileComponent } from '../../shared/ui/icon-tile/icon-tile.component';
import { EmptyStateComponent } from '../../shared/ui/empty-state/empty-state.component';
import type { Tone } from '../../shared/ui/tone';

const MONTHS_GEN = [
  'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
  'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря',
];

@Component({
    selector: 'app-notifications-tab',
    imports: [ClickableDirective, FormsModule, LoadingSpinnerComponent, IconComponent, IconTileComponent, EmptyStateComponent],
    changeDetection: ChangeDetectionStrategy.Eager,
    templateUrl: './notifications-tab.component.html',
    styleUrl: './notifications-tab.component.scss',
})
export class NotificationsTabComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly notificationState = inject(NotificationStateService);
  private readonly router = inject(Router);

  protected readonly icons = { Bell, CheckCheck, ChevronRight };

  items: AppNotification[] = [];
  unreadOnly = false;

  get hasUnread(): boolean {
    return this.items.some((n) => !n.isRead);
  }
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

  typeIcon(type: number): LucideIconData {
    return notificationTypeIcon(type);
  }

  typeTone(type: number): Tone {
    return notificationTypeTone(type);
  }

  /** RelatedEntityKind===null — тип оповещения без устоявшегося целевого экрана (напр.
   * BirthdayUpcoming/MedicationExpiring) — карточка не кликабельна, см. NotificationRelatedKind. */
  isClickable(n: AppNotification): boolean {
    return n.relatedEntityKind !== null;
  }

  /** Клик по карточке — сначала отмечаем прочитанным (как и явная кнопка), затем ведём на
   * связанный экран. Не блокируем навигацию ошибкой отметки — сама навигация важнее. */
  async openRelated(n: AppNotification): Promise<void> {
    if (n.relatedEntityKind === null) return;
    if (!n.isRead) await this.handleMarkRead(n.id);
    void this.router.navigate(relatedEntityRoute(n.relatedEntityKind, n.relatedEntityId), {
      queryParams: relatedEntityQueryParams(n.relatedEntityKind),
    });
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
