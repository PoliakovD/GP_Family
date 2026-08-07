import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, ApiError } from '../../services/api.service';
import { SearchResultItem, SearchResultType } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { BirthdayWidgetComponent } from '../birthday-widget/birthday-widget.component';

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
 * Главная (редизайн навигации, этап 3): точка входа с глобальным поиском — раньше эту роль
 * выполнял отдельный `/search` (см. историю `search.component.ts`), теперь он и есть Главная.
 * Список семей переехал на отдельную страницу `/families` (кнопка «Семьи» ниже) — так первый
 * экран не занят целиком списком семей.
 */
@Component({
  selector: 'app-home',
  standalone: true,
  imports: [RouterLink, LoadingSpinnerComponent, SearchFieldComponent, BirthdayWidgetComponent],
  templateUrl: './home.component.html',
})
export class HomeComponent {
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

  /**
   * Переключение чипа — серверный фильтр (не локальная фильтрация уже загруженного списка):
   * SearchService на бэкенде опрашивает только запрошенные источники, самый дорогой из них
   * (in-memory расшифровка медкарт) не трогается вовсе, если фильтр его исключает.
   */
  setFilter(value: SearchFilter): void {
    if (this.activeFilter === value) return;
    this.activeFilter = value;
    this.search.rerun(); // явный клик — без debounce, реагируем сразу
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
   * Лекарство ведёт прямо в его аптечку (контекст пришёл вместе с результатом — см.
   * MedicationContext на бэкенде), а не на общий список раздела, как было раньше. Анализы,
   * посещения врачей, дни рождения и справочник контекста карточки для глубокой ссылки не несут —
   * общий список раздела/пока не кликабельно (справочник: отдельного экрана просмотра ещё нет,
   * появится в этапе 4).
   */
  open(item: SearchResultItem): void {
    if (item.type === SearchResultType.Medication && item.medication) {
      void this.router.navigate(['/health/medications'], {
        queryParams: { familyId: item.medication.familyId, medkitId: item.medication.medkitId },
      });
      return;
    }
    if (item.type === SearchResultType.Record) {
      void this.router.navigateByUrl('/health/records');
      return;
    }
    if (item.type === SearchResultType.Visit) {
      void this.router.navigateByUrl('/health/visits');
      return;
    }
    if (item.type === SearchResultType.Birthday) {
      void this.router.navigateByUrl('/birthdays');
    }
  }
}
