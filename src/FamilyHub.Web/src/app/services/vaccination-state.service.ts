import { Injectable, inject, signal } from '@angular/core';
import { ApiError, ApiService } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import type { VaccinationScheduleItem, VaccinationSubject, VaccineCatalogSeries } from '../models/types';

/** Запрос на открытие модалки «Добавить прививку»; subject задан — сразу выбран получатель
 * (открыто с графика человека), не задан — пользователь выбирает в модалке (открыто из обзора). */
export interface VaccinationAddRequest {
  subjectKind?: 'user' | 'dependent';
  subjectId?: string;
}

/** Данные для шторки «Прививка сохранена» — что показать и кому предложить дневник. */
export interface VaccinationSavedInfo {
  subject: VaccinationSubject;
  item: VaccinationScheduleItem;
  reactionHint: string | null;
}

/**
 * Состояние раздела «Прививки» (макет «Screen - Vaccination») — зеркало IntakeStateService:
 * бейдж «требует внимания» в меню (карточки «Стоит запланировать» в обзоре), `version`,
 * растущий после любого изменения (открытые экраны перечитывают данные, не зная друг о друге), и
 * общая «шина» для модалки добавления/шторки после сохранения — их может открыть и обзор, и график
 * человека, а рисует одна страница-хаб (см. IntakePageComponent).
 */
@Injectable({ providedIn: 'root' })
export class VaccinationStateService {
  private readonly api = inject(ApiService);
  private readonly log = inject(DevLoggerService);

  /** Сколько прививок требуют внимания (скоро или можно сделать) — бейдж пункта меню. */
  readonly attention = signal(0);

  /** Растёт после любого изменения — экраны перечитывают данные. */
  readonly version = signal(0);

  readonly addRequest = signal<VaccinationAddRequest | null>(null);
  readonly savedInfo = signal<VaccinationSavedInfo | null>(null);

  /** Каталог (справочник серий) — статический, один раз на сессию: автокомплит формы,
   * подсказка о реакции для шторки «Сохранено», карточки справочника. */
  readonly catalog = signal<VaccineCatalogSeries[] | null>(null);
  private catalogPromise: Promise<VaccineCatalogSeries[]> | null = null;

  async refresh(): Promise<void> {
    try {
      const { count } = await this.api.getVaccinationAttentionCount();
      this.attention.set(count);
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      this.log.log('state', 'error', `getVaccinationAttentionCount failed: ${msg}`);
    }
  }

  /** Что-то изменилось: экраны перечитывают данные, бейдж обновляется. */
  changed(): void {
    this.version.update((v) => v + 1);
    void this.refresh();
  }

  openAdd(request: VaccinationAddRequest = {}): void {
    this.addRequest.set(request);
  }

  closeAdd(): void {
    this.addRequest.set(null);
  }

  showSaved(info: VaccinationSavedInfo): void {
    this.savedInfo.set(info);
  }

  closeSaved(): void {
    this.savedInfo.set(null);
  }

  async ensureCatalog(): Promise<VaccineCatalogSeries[]> {
    const cached = this.catalog();
    if (cached) return cached;
    this.catalogPromise ??= this.api.getVaccineCatalog().then((list) => {
      this.catalog.set(list);
      return list;
    });
    return this.catalogPromise;
  }

  reactionHintFor(seriesCode: string): string | null {
    return this.catalog()?.find((s) => s.code === seriesCode)?.reactionHint ?? null;
  }
}
