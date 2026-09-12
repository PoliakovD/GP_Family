import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService, ApiError } from './api.service';
import { DevLoggerService } from './dev-logger.service';
import type { ActiveJobsSummaryResponse } from '../models/types';

/** Пусто — до первого refresh() или пока ни одной активной задачи ни в одной из четырёх групп. */
const EMPTY_SUMMARY: ActiveJobsSummaryResponse = {
  extraction: { total: 0, items: [] },
  labAnalyte: { total: 0, items: [] },
  medication: { total: 0, items: [] },
  visitMedication: { total: 0, items: [] },
};

/**
 * Глобальный индикатор фоновых процессов (§4 плана «живой конвейер») — бейдж + выпадающий список,
 * видимые из любого экрана приложения, не только со страницы конкретной записи (в отличие от
 * pipeline-progress, который живёт пока открыта именно она). По форме — как NotificationStateService,
 * но с АДАПТИВНЫМ поллингом, не вечным: пока хотя бы одна задача активна — опрашиваем каждые 5с,
 * как только активных задач не осталось — сами останавливаемся (тот же принцип, что у
 * resumeLivePolling в medical-records-panel, только на уровень выше — здесь про ВСЕ записи
 * пользователя, а не про одну открытую).
 */
@Injectable({ providedIn: 'root' })
export class BackgroundJobsStateService {
  private readonly api = inject(ApiService);
  private readonly log = inject(DevLoggerService);

  readonly summary = signal<ActiveJobsSummaryResponse>(EMPTY_SUMMARY);

  readonly totalActive = computed(() => {
    const s = this.summary();
    return s.extraction.total + s.labAnalyte.total + s.medication.total + s.visitMedication.total;
  });

  private pollHandle: ReturnType<typeof setInterval> | null = null;
  private readonly pollIntervalMs = 5000;

  async refresh(): Promise<void> {
    try {
      const summary = await this.api.getActiveJobsSummary();
      this.summary.set(summary);
    } catch (err) {
      // Бейдж — вспомогательная индикация, не критичный путь (тот же принцип, что и
      // NotificationStateService.refresh) — транзиентный сбой молча оставляет предыдущее значение.
      const msg = err instanceof ApiError ? err.message : String(err);
      this.log.log('state', 'error', `getActiveJobsSummary failed: ${msg}`);
      return;
    }

    if (this.totalActive() > 0) this.ensurePolling();
    else this.stopPolling();
  }

  private ensurePolling(): void {
    if (this.pollHandle !== null) return;
    this.pollHandle = setInterval(() => void this.refresh(), this.pollIntervalMs);
  }

  private stopPolling(): void {
    if (this.pollHandle === null) return;
    clearInterval(this.pollHandle);
    this.pollHandle = null;
  }
}
