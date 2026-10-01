import { Injectable, computed, inject, signal } from '@angular/core';
import { AdminApiService, ReviewQueueCounts } from '../../../services/admin-api.service';

/**
 * Счётчики очереди «Одобрение» (ADR-0018) — одно состояние на всю админку: бейдж в меню хаба и
 * вкладки самой страницы читают одно и то же. Обновляется при открытии хаба и после каждого
 * действия в очереди; ошибка загрузки молча оставляет прежние числа (бейдж — подсказка, не данные).
 */
@Injectable({ providedIn: 'root' })
export class ReviewQueueStore {
  private readonly api = inject(AdminApiService);

  readonly counts = signal<ReviewQueueCounts | null>(null);
  readonly searches = computed(() => this.counts()?.searches ?? 0);
  readonly results = computed(() => this.counts()?.results ?? 0);
  readonly total = computed(() => this.searches() + this.results());

  async refresh(): Promise<void> {
    try {
      this.counts.set(await this.api.getReviewCounts());
    } catch {
      // Не критично — см. class doc.
    }
  }
}
