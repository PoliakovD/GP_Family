import { Component, OnInit, inject } from '@angular/core';
import { ApiService, ApiError } from '../../services/api.service';
import type { KbListItem, KbMedicationCard } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { KbCardComponent } from '../kb-card/kb-card.component';

/**
 * Page (таксономия — patterns/frontend_web.md): общий обезличенный справочник препаратов
 * (этап 4), вкладка «Справочник» хаба «Здоровье». Без запроса — первая страница справочника
 * по алфавиту (наполняется автоматически при сохранении лекарств в Аптечке); с запросом от
 * 2 символов — серверный поиск (тот же tsvector+pg_trgm источник, что и общий поиск на Главной,
 * см. SearchService).
 */
@Component({
  selector: 'app-kb-tab',
  standalone: true,
  imports: [SearchFieldComponent, LoadingSpinnerComponent, BottomSheetComponent, KbCardComponent],
  templateUrl: './kb-tab.component.html',
})
export class KbTabComponent implements OnInit {
  private readonly api = inject(ApiService);

  loading = true;
  error: string | null = null;
  defaultItems: KbListItem[] = [];

  readonly search = new DebouncedSearch<KbListItem>(
    (q) => this.api.searchKb(q).then((r) => r.items),
    (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
  );

  cardOpen = false;
  cardLoading = false;
  cardError: string | null = null;
  selectedCard: KbMedicationCard | null = null;

  get items(): KbListItem[] {
    return this.search.query.trim().length > 0 ? this.search.items : this.defaultItems;
  }

  get showingSearch(): boolean {
    return this.search.query.trim().length > 0;
  }

  async ngOnInit(): Promise<void> {
    await this.loadDefault();
  }

  onQueryChange(value: string): void {
    this.search.query = value;
    this.search.onQueryChange();
  }

  async openCard(id: string): Promise<void> {
    this.cardOpen = true;
    this.cardLoading = true;
    this.cardError = null;
    this.selectedCard = null;
    try {
      this.selectedCard = await this.api.getKbMedication(id);
    } catch (err) {
      this.cardError = err instanceof ApiError ? err.message : 'Не удалось загрузить карточку препарата.';
    } finally {
      this.cardLoading = false;
    }
  }

  closeCard(): void {
    this.cardOpen = false;
  }

  private async loadDefault(): Promise<void> {
    this.loading = true;
    try {
      const response = await this.api.searchKb();
      this.defaultItems = response.items;
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить справочник.';
    } finally {
      this.loading = false;
    }
  }
}
