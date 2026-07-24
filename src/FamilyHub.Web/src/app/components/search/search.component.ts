import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { SearchResultItem, SearchResultType } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';

/** Синхронизировано с SearchService.MinQueryLength на бэкенде — короче не дёргаем API вовсе. */
const MIN_QUERY_LENGTH = 2;
const DEBOUNCE_MS = 300;

const TYPE_LABEL: Record<number, string> = {
  [SearchResultType.Medication]: 'Лекарство',
  [SearchResultType.Kb]: 'Справочник',
  [SearchResultType.Record]: 'Анализ',
};

const TYPE_ICON: Record<number, string> = {
  [SearchResultType.Medication]: 'ph-duotone ph-first-aid-kit',
  [SearchResultType.Kb]: 'ph-duotone ph-book-open-text',
  [SearchResultType.Record]: 'ph-duotone ph-heartbeat',
};

/** Значения чипов-фильтров — 'all' убирает параметр types вовсе (см. api.service.ts). */
export type SearchFilter = 'all' | SearchResultType;

/** Токен для query-параметра `types` — имена enum'а на бэкенде (Search/SearchDtos.cs), регистр не важен. */
const TYPE_QUERY_TOKEN: Record<number, string> = {
  [SearchResultType.Medication]: 'medication',
  [SearchResultType.Kb]: 'kb',
  [SearchResultType.Record]: 'record',
};

const FILTER_CHIPS: { value: SearchFilter; label: string }[] = [
  { value: 'all', label: 'Все' },
  { value: SearchResultType.Medication, label: 'Лекарства' },
  { value: SearchResultType.Kb, label: 'Справочник' },
  { value: SearchResultType.Record, label: 'Анализы' },
];

/**
 * Куда ведёт клик по результату. Бэкенд не отдаёт семью/аптечку результата (только Id записи) —
 * попадаем на общий список раздела, а не на конкретную карточку. Справочник (Kb) — пока некликабелен:
 * отдельного экрана просмотра справочника ещё нет (появится в этапе 4, конвейер обогащения).
 */
const TYPE_ROUTE: Partial<Record<number, string>> = {
  [SearchResultType.Medication]: '/health/medications',
  [SearchResultType.Record]: '/health/records',
};

@Component({
  selector: 'app-search',
  standalone: true,
  imports: [FormsModule, LoadingSpinnerComponent],
  templateUrl: './search.component.html',
})
export class SearchComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly filterChips = FILTER_CHIPS;
  activeFilter: SearchFilter = 'all';

  query = '';
  items: SearchResultItem[] = [];
  loading = false;
  error: string | null = null;
  /** true после первого выполненного запроса — отличает «ещё не искали» от «искали, пусто». */
  searched = false;

  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  /** Отбрасываем ответы устаревших запросов, если пользователь печатает быстрее ответа сети. */
  private requestSeq = 0;

  onQueryChange(): void {
    if (this.debounceTimer) clearTimeout(this.debounceTimer);

    const q = this.query.trim();
    if (q.length < MIN_QUERY_LENGTH) {
      this.requestSeq++; // инвалидируем в полёте ответ на предыдущий, более длинный запрос
      this.items = [];
      this.searched = false;
      this.error = null;
      this.loading = false;
      return;
    }

    this.debounceTimer = setTimeout(() => this.runSearch(q), DEBOUNCE_MS);
  }

  /**
   * Переключение чипа — серверный фильтр (не локальная фильтрация уже загруженного списка):
   * SearchService на бэкенде опрашивает только запрошенные источники, самый дорогой из них
   * (in-memory расшифровка медкарт) не трогается вовсе, если фильтр его исключает.
   */
  setFilter(value: SearchFilter): void {
    if (this.activeFilter === value) return;
    this.activeFilter = value;

    const q = this.query.trim();
    if (q.length < MIN_QUERY_LENGTH) return;

    if (this.debounceTimer) clearTimeout(this.debounceTimer);
    void this.runSearch(q); // явный клик — без debounce, реагируем сразу
  }

  private async runSearch(q: string): Promise<void> {
    const seq = ++this.requestSeq;
    this.loading = true;
    try {
      const types = this.activeFilter === 'all' ? undefined : TYPE_QUERY_TOKEN[this.activeFilter];
      const response = await this.api.search(q, types);
      if (seq !== this.requestSeq) return;
      this.items = response.items;
      this.error = null;
    } catch (err) {
      if (seq !== this.requestSeq) return;
      this.error = err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.';
    } finally {
      if (seq === this.requestSeq) {
        this.loading = false;
        this.searched = true;
      }
    }
  }

  typeLabel(type: number): string {
    return TYPE_LABEL[type] ?? 'Результат';
  }

  typeIcon(type: number): string {
    return TYPE_ICON[type] ?? 'ph-duotone ph-magnifying-glass';
  }

  isNavigable(type: number): boolean {
    return type in TYPE_ROUTE;
  }

  open(item: SearchResultItem): void {
    const route = TYPE_ROUTE[item.type];
    if (route) void this.router.navigateByUrl(route);
  }
}
