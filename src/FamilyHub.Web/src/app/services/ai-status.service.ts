import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from './api.service';

/** Как часто спрашиваем, жив ли ИИ (LM Studio). Бэкенд кэширует пинг на 10 с — чаще смысла нет. */
const POLL_INTERVAL_MS = 20_000;

/**
 * Доступность локального ИИ (LM Studio) для глобальной плашки «ИИ временно недоступен» и подписей
 * «ждём ИИ» на записях/резюме. Пока сервер лежит, пользовательские LLM-задачи не теряются, а ждут в
 * очереди (см. LmStudioRecoverySweepJob) — интерфейс должен это показывать, а не молчать. Поллинг
 * дешёвый (флаг) и вечный, пока приложение открыто: заранее неизвестно, когда ноутбук уснёт.
 */
@Injectable({ providedIn: 'root' })
export class AiStatusService {
  private readonly api = inject(ApiService);

  /** null — ещё не спрашивали (или сеть недоступна): плашку не показываем, пока не знаем точно. */
  readonly available = signal<boolean | null>(null);
  readonly unavailable = computed(() => this.available() === false);

  private handle: ReturnType<typeof setInterval> | null = null;

  /** Идемпотентно: запускает поллинг один раз и сразу делает первый запрос. */
  start(): void {
    if (this.handle !== null) return;
    void this.refresh();
    this.handle = setInterval(() => void this.refresh(), POLL_INTERVAL_MS);
  }

  async refresh(): Promise<void> {
    try {
      this.available.set((await this.api.getAiStatus()).available);
    } catch {
      // Сбой самого запроса (нет сети, 401) — не то же самое, что «ИИ недоступен»; оставляем прежнее.
    }
  }
}
