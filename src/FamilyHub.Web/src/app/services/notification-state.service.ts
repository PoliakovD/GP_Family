import {Injectable, inject, signal} from '@angular/core';
import {ApiService, ApiError} from './api.service';
import {DevLoggerService} from './dev-logger.service';

/**
 * Счётчик непрочитанных уведомлений для бейджа (редизайн v2 — сайдбар/пункт «Уведомления»,
 * мобильный таб «Ещё»). Отдельный сервис, не часть FamilyStateService — источник данных другой
 * (GET /api/notifications/unread-count, не связан с семьями) и обновляется по другим триггерам
 * (навигация, прочтение уведомления), а не по циклу жизни auth/семей.
 */
@Injectable({providedIn: 'root'})
export class NotificationStateService {
    private readonly api = inject(ApiService);
    private readonly log = inject(DevLoggerService);

    readonly unread = signal(0);

    async refresh(): Promise<void> {
        try {
            const {count} = await this.api.getUnreadNotificationCount();
            this.unread.set(count);
        } catch (err) {
            // Бейдж — вспомогательная индикация, не критичный путь: транзиентный сбой не должен
            // ронять остальной каркас или показывать ошибку пользователю (тот же принцип, что и
            // BirthdayWidgetComponent — молча оставляем предыдущее значение).
            const msg = err instanceof ApiError ? err.message : String(err);
            this.log.log('state', 'error', `getUnreadNotificationCount failed: ${msg}`);
        }
    }

    /** Локальный оптимистичный декремент сразу после прочтения одного уведомления — не ждать
     * следующего refresh() (навигации), чтобы бейдж не отставал на экране, где сам список открыт. */
    decrementLocal(): void {
        this.unread.update((n) => Math.max(0, n - 1));
    }
}
