import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiService, ApiError } from '../../services/api.service';
import type { KbAnalyteCard, KbAnalyteListItem } from '../../models/types';
import { SpecimenType } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { specimenLabel } from '../../shared/util/specimen';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { BreakpointService } from '../../services/breakpoint.service';
import { IndicatorInfoComponent } from '../indicator-info/indicator-info.component';
import { IndicatorInfoPanelComponent } from '../indicator-info/indicator-info-panel.component';

/**
 * Page (таксономия — patterns/frontend_web.md): справочник показателей анализов (редизайн v2,
 * PR4) — зеркало KbTabComponent (препараты) на другую таблицу. Открытие статьи переиспользует
 * `<app-indicator-info>` без reading/history (точки входа 2/3 плана — чип "что смотрят вместе"
 * и сам каталог используют один и тот же путь), и тот же приём двух обёрток (десктоп-панель /
 * мобильная шторка), что medical-records-panel для персонализированного просмотра.
 *
 * `?id=` в query — точка входа из "Открыть в справочнике" статьи показателя записи (footer
 * indicator-info, medical-records-panel): при монтировании сразу открывает эту статью, не
 * заставляя пользователя искать её заново в списке.
 */
@Component({
  selector: 'app-kb-analyte-tab',
  standalone: true,
  imports: [SearchFieldComponent, LoadingSpinnerComponent, BottomSheetComponent, IndicatorInfoComponent, IndicatorInfoPanelComponent],
  templateUrl: './kb-analyte-tab.component.html',
})
export class KbAnalyteTabComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointService);
  private queryParamsSub?: Subscription;

  loading = true;
  error: string | null = null;
  defaultItems: KbAnalyteListItem[] = [];

  readonly search = new DebouncedSearch<KbAnalyteListItem>(
    (q) => this.api.searchKbAnalytes(q).then((r) => r.items),
    (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
  );

  cardOpen = false;
  cardLoading = false;
  cardError: string | null = null;
  selectedCard: KbAnalyteCard | null = null;

  get items(): KbAnalyteListItem[] {
    return this.search.query.trim().length > 0 ? this.search.items : this.defaultItems;
  }

  get showingSearch(): boolean {
    return this.search.query.trim().length > 0;
  }

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  /** Ключ справочника — (показатель, биоматериал): одно DisplayName может встретиться дважды
   * («Белок» в крови и в моче) — показывать только когда биоматериал известен и в списке есть
   * ещё одна запись с тем же именем (иначе подпись не несёт пользы, например "Гемоглобин · Кровь"
   * когда мочевого гемоглобина в справочнике вовсе нет). */
  specimenSubtitle(item: KbAnalyteListItem): string | null {
    if (item.specimen === SpecimenType.Unknown) return null;
    const sameName = this.items.filter((i) => i.displayName === item.displayName);
    return sameName.length > 1 ? specimenLabel(item.specimen) : null;
  }

  async ngOnInit(): Promise<void> {
    await this.loadDefault();

    // ?id= — переход "Открыть в справочнике" из статьи конкретного показателя записи.
    this.queryParamsSub = this.route.queryParamMap.subscribe((params) => {
      const id = params.get('id');
      if (id && this.selectedCard?.id !== id) void this.openCard(id);
    });
  }

  ngOnDestroy(): void {
    this.queryParamsSub?.unsubscribe();
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
      this.selectedCard = await this.api.getKbAnalyte(id);
    } catch (err) {
      this.cardError = err instanceof ApiError ? err.message : 'Не удалось загрузить статью справочника.';
    } finally {
      this.cardLoading = false;
    }
  }

  closeCard(): void {
    this.cardOpen = false;
    // Сброс ?id= — иначе повторный клик по тому же чипу из другого места не откроет шторку снова
    // (тот же id, ngOnInit-подписка не увидела бы изменения).
    void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  private async loadDefault(): Promise<void> {
    this.loading = true;
    try {
      const response = await this.api.searchKbAnalytes();
      this.defaultItems = response.items;
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить справочник.';
    } finally {
      this.loading = false;
    }
  }
}
