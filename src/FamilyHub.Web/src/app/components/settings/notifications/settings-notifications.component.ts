import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../services/auth.service';
import { ApiService, ApiError } from '../../../services/api.service';
import { PushNotificationService } from '../../../services/push-notification.service';
import { TelegramService } from '../../../services/telegram.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { NotificationType, type NotificationPreference } from '../../../models/types';
import { notificationTypeLabel } from '../../../shared/util/notification-type-labels';

/** Все известные типы оповещений — бэкенд отдаёт полную матрицу, но на случай рассинхрона
 * (новый тип добавлен на бэке, фронт ещё не задеплоен) итерируем по собственному списку. */
const ALL_TYPES = Object.values(NotificationType);

/**
 * Вкладка «Уведомления»: push-тумблер (устройство целиком) + тонкая настройка по типам оповещений
 * (push/Telegram раздельно). Запись в ленте /notifications создаётся всегда — здесь только про
 * канал доставки, см. FamilyHub.Domain.Entities.UserNotificationPreference.
 */
@Component({
  selector: 'app-settings-notifications',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './settings-notifications.component.html',
})
export class SettingsNotificationsComponent implements OnInit {
  readonly auth = inject(AuthService);
  readonly push = inject(PushNotificationService);
  readonly tg = inject(TelegramService);
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  readonly pushBusy = signal(false);
  readonly prefsBusy = signal(false);
  readonly preferences = signal<NotificationPreference[] | null>(null);

  async ngOnInit(): Promise<void> {
    await this.auth.loadMe();
    void this.push.refreshStatus();
    try {
      this.preferences.set(await this.api.getNotificationPreferences());
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось загрузить настройки уведомлений.');
    }
  }

  label(type: number): string {
    return notificationTypeLabel(type);
  }

  get rows(): NotificationPreference[] {
    const known = this.preferences() ?? [];
    return ALL_TYPES.map(
      (type) => known.find((p) => p.type === type) ?? { type, pushEnabled: true, telegramEnabled: true },
    );
  }

  async togglePush(): Promise<void> {
    this.pushBusy.set(true);
    try {
      if (this.push.isSubscribed()) {
        await this.push.unsubscribe();
        this.toast.success('Push-уведомления отключены.');
      } else {
        await this.push.subscribe();
        this.toast.success('Push-уведомления включены.');
      }
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось изменить push-уведомления.');
    } finally {
      this.pushBusy.set(false);
    }
  }

  async setPreference(type: number, field: 'pushEnabled' | 'telegramEnabled', value: boolean): Promise<void> {
    const next = this.rows.map((r) => (r.type === type ? { ...r, [field]: value } : r));
    this.preferences.set(next);

    this.prefsBusy.set(true);
    try {
      await this.api.saveNotificationPreferences(next);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось сохранить настройки уведомлений.');
    } finally {
      this.prefsBusy.set(false);
    }
  }
}
