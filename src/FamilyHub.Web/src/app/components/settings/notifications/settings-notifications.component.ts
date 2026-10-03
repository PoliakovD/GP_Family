import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../services/auth.service';
import { ApiService, ApiError } from '../../../services/api.service';
import { PushNotificationService } from '../../../services/push-notification.service';
import { TelegramService } from '../../../services/telegram.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { NotificationType, type NotificationPreference } from '../../../models/types';
import { notificationTypeLabel, notificationTypeSection } from '../../../shared/util/notification-type-labels';

/** Все известные типы оповещений — бэкенд отдаёт полную матрицу, но на случай рассинхрона
 * (новый тип добавлен на бэке, фронт ещё не задеплоен) итерируем по собственному списку. */
const ALL_TYPES = Object.values(NotificationType);

/**
 * Вкладка «Уведомления»: push-тумблер (устройство целиком) + тонкая настройка по типам оповещений
 * (push/Telegram раздельно). Запись в ленте /notifications создаётся всегда — здесь только про
 * канал доставки, см. FamilyHub.Domain.Entities.UserNotificationPreference.
 */
function isIos(): boolean {
  const ua = navigator.userAgent;
  return /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isStandalone(): boolean {
  return window.matchMedia?.('(display-mode: standalone)').matches || (navigator as Navigator & { standalone?: boolean }).standalone === true;
}

@Component({
    selector: 'app-settings-notifications',
    imports: [FormsModule],
    templateUrl: './settings-notifications.component.html',
    styleUrl: './settings-notifications.component.scss'
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
  /** Не загрузились настройки — показываем «Повторить» вместо вечного «Загрузка…». */
  readonly prefsLoadFailed = signal(false);

  /** iPhone/iPad вне установленного на «Домой» приложения: Safari не даёт веб-уведомлений вовсе,
   * и раньше человек видел «браузер не поддерживает» без подсказки, что делать. */
  readonly iosNeedsInstall = isIos() && !isStandalone();
  /** Пользователь однажды нажал «Запретить» — браузер больше не спросит, нужна инструкция. */
  get permissionDenied(): boolean {
    return typeof Notification !== 'undefined' && Notification.permission === 'denied';
  }

  async ngOnInit(): Promise<void> {
    await this.auth.loadMe();
    void this.push.refreshStatus();
    await this.loadPreferences();
  }

  async loadPreferences(): Promise<void> {
    this.prefsLoadFailed.set(false);
    try {
      this.preferences.set(await this.api.getNotificationPreferences());
    } catch (e) {
      this.prefsLoadFailed.set(true);
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

  /** Группировка по разделу (редизайн v3, PR8) — "Аптечка"/"Семья"/"Доступ к записям" вместо
   * плоской таблицы; порядок строк внутри секции — как в ALL_TYPES (порядок enum'а). */
  get sections(): { name: string; rows: NotificationPreference[] }[] {
    const groups = new Map<string, NotificationPreference[]>();
    for (const row of this.rows) {
      const section = notificationTypeSection(row.type);
      (groups.get(section) ?? groups.set(section, []).get(section)!).push(row);
    }
    return [...groups.entries()].map(([name, rows]) => ({ name, rows }));
  }

  async togglePush(): Promise<void> {
    this.pushBusy.set(true);
    try {
      if (this.push.isSubscribed()) {
        await this.push.unsubscribe();
        this.toast.success('Уведомления на этом устройстве выключены.');
      } else {
        await this.push.subscribe();
        this.toast.success('Уведомления на этом устройстве включены.');
      }
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось изменить уведомления.');
    } finally {
      this.pushBusy.set(false);
    }
  }

  async setPreference(type: number, field: 'pushEnabled' | 'telegramEnabled', value: boolean): Promise<void> {
    const previous = this.preferences();
    const next = this.rows.map((r) => (r.type === type ? { ...r, [field]: value } : r));
    this.preferences.set(next);

    this.prefsBusy.set(true);
    try {
      await this.api.saveNotificationPreferences(next);
    } catch (e) {
      // Откат: иначе тумблер показывал состояние, которое не сохранилось.
      this.preferences.set(previous);
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось сохранить настройки уведомлений.');
    } finally {
      this.prefsBusy.set(false);
    }
  }
}
