import type { ExtractionStatusResponse } from '../../models/types';
import { EXTRACTION_TERMINAL_STATUSES } from './extraction-pipeline';

export interface ExtractionPollerHooks<T extends { id: string }> {
  fetchStatus(item: T): Promise<ExtractionStatusResponse>;
  /** Каждый успешный ответ — до решения об остановке. */
  onStatus(item: T, status: ExtractionStatusResponse): void;
  /** Терминальный статус (Completed/Failed/Skipped); опрос этой записи уже остановлен. */
  onFinished(item: T, status: ExtractionStatusResponse): void | Promise<void>;
  /** maxFailures неудачных запросов подряд — опрос остановлен, похоже на настоящий обрыв связи. */
  onGaveUp(item: T, error: unknown): void;
}

export interface ExtractionPollerOptions {
  intervalMs: number;
  /** Пока задача ждёт возвращения ИИ (может быть часами) — опрашиваем реже. */
  waitingIntervalMs: number;
  maxFailures: number;
}

/**
 * Опрос статуса распознавания по записям (вынесен из MedicalRecordsPanelComponent).
 *
 * - Сбой ОДНОГО запроса (моргнула сеть, вкладка была в фоне) не останавливает опрос: задача на
 *   бэкенде идёт независимо. Сдаёмся только после maxFailures неудач подряд.
 * - Пока задача ждёт ИИ, интервал увеличивается до waitingIntervalMs и возвращается обратно.
 * - На терминальном статусе опрос останавливается до вызова onFinished.
 */
export class ExtractionPoller<T extends { id: string }> {
  private readonly handles = new Map<string, ReturnType<typeof setInterval>>();
  private readonly failures = new Map<string, number>();
  private readonly slow = new Set<string>();

  constructor(
    private readonly hooks: ExtractionPollerHooks<T>,
    private readonly options: ExtractionPollerOptions,
  ) {}

  isPolling(id: string): boolean {
    return this.handles.has(id);
  }

  start(item: T): void {
    this.clearTimer(item.id);
    this.failures.delete(item.id);
    this.slow.delete(item.id);
    void this.tick(item);
    this.handles.set(item.id, setInterval(() => void this.tick(item), this.options.intervalMs));
  }

  stop(id: string): void {
    this.clearTimer(id);
    this.failures.delete(id);
    this.slow.delete(id);
  }

  stopAll(): void {
    for (const handle of this.handles.values()) clearInterval(handle);
    this.handles.clear();
    this.failures.clear();
    this.slow.clear();
  }

  private clearTimer(id: string): void {
    const handle = this.handles.get(id);
    if (handle) clearInterval(handle);
    this.handles.delete(id);
  }

  private async tick(item: T): Promise<void> {
    let status: ExtractionStatusResponse;
    try {
      status = await this.hooks.fetchStatus(item);
    } catch (error) {
      const count = (this.failures.get(item.id) ?? 0) + 1;
      if (count < this.options.maxFailures) {
        this.failures.set(item.id, count);
        return;
      }
      this.stop(item.id);
      this.hooks.onGaveUp(item, error);
      return;
    }

    this.failures.delete(item.id);
    this.hooks.onStatus(item, status);

    if (EXTRACTION_TERMINAL_STATUSES.includes(status.status)) {
      this.stop(item.id);
      await this.hooks.onFinished(item, status);
      return;
    }

    // Переключение «ждём ИИ» ↔ обычный режим — перезапускаем интервал с другим периодом.
    if (status.waitingForAi !== this.slow.has(item.id) && this.handles.has(item.id)) {
      if (status.waitingForAi) this.slow.add(item.id); else this.slow.delete(item.id);
      this.clearTimer(item.id);
      this.handles.set(item.id, setInterval(
        () => void this.tick(item),
        status.waitingForAi ? this.options.waitingIntervalMs : this.options.intervalMs,
      ));
    }
  }
}
