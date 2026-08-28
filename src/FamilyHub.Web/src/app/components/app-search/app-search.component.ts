import { Component, HostListener, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { SearchResultItem, SearchResultType } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';

const TYPE_LABEL: Record<number, string> = {
  [SearchResultType.Medication]: 'Лекарство',
  [SearchResultType.Kb]: 'Справочник',
  [SearchResultType.Record]: 'Анализ',
  [SearchResultType.Birthday]: 'День рождения',
  [SearchResultType.Visit]: 'Приём у врача',
};

const TYPE_ICON: Record<number, string> = {
  [SearchResultType.Medication]: 'ph-duotone ph-first-aid-kit',
  [SearchResultType.Kb]: 'ph-duotone ph-book-open-text',
  [SearchResultType.Record]: 'ph-duotone ph-heartbeat',
  [SearchResultType.Birthday]: 'ph-fill ph-cake',
  [SearchResultType.Visit]: 'ph-duotone ph-stethoscope',
};

/** Значения чипов-фильтров — 'all' убирает параметр types вовсе (см. api.service.ts). */
export type SearchFilter = 'all' | SearchResultType;

/** Токен для query-параметра `types` — имена enum'а на бэкенде (Search/SearchDtos.cs), регистр не важен. */
const TYPE_QUERY_TOKEN: Record<number, string> = {
  [SearchResultType.Medication]: 'medication',
  [SearchResultType.Kb]: 'kb',
  [SearchResultType.Record]: 'record',
  [SearchResultType.Birthday]: 'birthday',
  [SearchResultType.Visit]: 'visit',
};

const FILTER_CHIPS: { value: SearchFilter; label: string }[] = [
  { value: 'all', label: 'Все' },
  { value: SearchResultType.Medication, label: 'Лекарства' },
  { value: SearchResultType.Kb, label: 'Справочник' },
  { value: SearchResultType.Record, label: 'Анализы' },
  { value: SearchResultType.Visit, label: 'Врачи' },
  { value: SearchResultType.Birthday, label: 'Дни рождения' },
];

/**
 * Поиск в верхней строке каркаса (редизайн v2, PR2) — извлечён из `HomeComponent`, который раньше
 * единолично владел поиском (см. `.claude/plans/...`). Логика типов/иконок/навигации и
 * `DebouncedSearch` не менялись — перенесены как есть. Результаты рендерятся оверлеем под полем
 * (`position:absolute` в `.scss`), видимым, пока в поле есть текст — закрывается очисткой поля
 * (Escape тоже очищает) или переходом по результату.
 */
@Component({
  selector: 'app-search',
  standalone: true,
  imports: [RouterLink, LoadingSpinnerComponent, SearchFieldComponent],
  templateUrl: './app-search.component.html',
  styleUrl: './app-search.component.scss',
})
export class AppSearchComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly filterChips = FILTER_CHIPS;
  activeFilter: SearchFilter = 'all';

  readonly search = new DebouncedSearch<SearchResultItem>(
    (q) => {
      const types = this.activeFilter === 'all' ? undefined : TYPE_QUERY_TOKEN[this.activeFilter];
      return this.api.search(q, types).then((r) => r.items);
    },
    (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
  );

  onQueryChange(value: string): void {
    this.search.query = value;
    this.search.onQueryChange();
  }

  /** Переключение чипа — серверный фильтр (см. HomeComponent, откуда перенесена логика): не
   * запрошенный источник SearchService вообще не трогает. */
  setFilter(value: SearchFilter): void {
    if (this.activeFilter === value) return;
    this.activeFilter = value;
    this.search.rerun();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.search.reset();
  }

  typeLabel(type: number): string {
    return TYPE_LABEL[type] ?? 'Результат';
  }

  typeIcon(type: number): string {
    return TYPE_ICON[type] ?? 'ph-duotone ph-magnifying-glass';
  }

  isNavigable(item: SearchResultItem): boolean {
    return (
      item.type === SearchResultType.Medication ||
      item.type === SearchResultType.Record ||
      item.type === SearchResultType.Visit ||
      item.type === SearchResultType.Birthday
    );
  }

  /**
   * Лекарство ведёт прямо в его аптечку (контекст пришёл вместе с результатом), а не на общий
   * список раздела. Анализы/посещения/дни рождения — на общий список раздела; справочник пока не
   * кликабелен (отдельного экрана просмотра статьи не было — см. PR4, kb-analyte-tab). Поиск
   * очищается после перехода — топбар персистентен между страницами, в отличие от прежней Главной.
   */
  open(item: SearchResultItem): void {
    if (item.type === SearchResultType.Medication && item.medication) {
      void this.router.navigate(['/health/medications'], {
        queryParams: { familyId: item.medication.familyId, medkitId: item.medication.medkitId },
      });
    } else if (item.type === SearchResultType.Record) {
      void this.router.navigateByUrl('/health/records');
    } else if (item.type === SearchResultType.Visit) {
      void this.router.navigateByUrl('/health/visits');
    } else if (item.type === SearchResultType.Birthday) {
      void this.router.navigateByUrl('/birthdays');
    } else {
      return;
    }
    this.search.reset();
  }
}
