import { Injectable, inject, signal } from '@angular/core';
import { ApiError, ApiService } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import type { CourseSummary } from '../models/types';

/** Запрос на открытие формы курса: править существующий (courseId) либо создать новый — пустым, для
 * подопечного (dependentId) или из назначения врача (recordId + prescriptionIndex). */
export interface IntakeFormRequest {
  courseId?: string;
  dependentId?: string | null;
  recordId?: string;
  prescriptionIndex?: number;
}

/**
 * Состояние раздела «Приём лекарств» (макет «Screen - Medication schedule»): бейдж «требует внимания» в
 * меню, список активных курсов и «шина» между страницами и общими панелями. Форма курса и настройки
 * напоминаний открываются из разных экранов (Сегодня, Курсы, карточка), а рисует их одна страница-хаб,
 * поэтому запрос на открытие — сигнал здесь, а `version` растёт после любого изменения — все экраны
 * перечитывают свои данные, не зная друг о друге.
 */
@Injectable({ providedIn: 'root' })
export class IntakeStateService {
  private readonly api = inject(ApiService);
  private readonly log = inject(DevLoggerService);

  /** Сколько приёмов требуют внимания сейчас (наступили или пропущены) — бейдж пункта меню. */
  readonly attention = signal(0);

  /** Растёт после любого изменения курса/приёма/настроек — экраны перечитывают данные. */
  readonly version = signal(0);

  /** Активные и приостановленные курсы (для счётчика «Курсы · N» и списка). null — ещё не загружены. */
  readonly courses = signal<CourseSummary[] | null>(null);

  readonly form = signal<IntakeFormRequest | null>(null);
  readonly remindersOpen = signal(false);

  private timer: ReturnType<typeof setInterval> | null = null;
  private lastSyncedZone: string | null = null;

  /** Обновляет бейдж; сбой не критичен — оставляем прежнее значение, как у счётчика уведомлений. */
  async refresh(): Promise<void> {
    try {
      const { count } = await this.api.getIntakeAttentionCount();
      this.attention.set(count);
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      this.log.log('state', 'error', `getIntakeAttentionCount failed: ${msg}`);
    }
  }

  async loadCourses(): Promise<void> {
    try {
      this.courses.set(await this.api.getCourses());
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      this.log.log('state', 'error', `getCourses failed: ${msg}`);
    }
  }

  /** Что-то изменилось: экраны перечитывают данные, бейдж обновляется. */
  changed(): void {
    this.version.update((v) => v + 1);
    void this.refresh();
  }

  openForm(request: IntakeFormRequest = {}): void {
    this.form.set(request);
  }

  closeForm(): void {
    this.form.set(null);
  }

  /** Раз в 5 минут — приёмы «наступают» без действий пользователя, бейдж не должен отставать. */
  startPolling(): void {
    if (this.timer !== null) return;
    this.timer = setInterval(() => void this.refresh(), 5 * 60_000);
  }

  /** Часовой пояс браузера → сервер (от него «8:00» в расписании и «сегодня»). Только если отличается от
   * известного серверу; сбой не критичен — повторится при следующем входе. */
  async syncTimeZone(known: string | null | undefined): Promise<void> {
    let zone: string | undefined;
    try {
      zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    } catch {
      return;
    }
    if (!zone || zone === known || zone === this.lastSyncedZone) return;
    this.lastSyncedZone = zone;
    try {
      await this.api.setTimeZone(zone);
    } catch (err) {
      this.lastSyncedZone = null;
      const msg = err instanceof ApiError ? err.message : String(err);
      this.log.log('state', 'error', `setTimeZone failed: ${msg}`);
    }
  }
}
