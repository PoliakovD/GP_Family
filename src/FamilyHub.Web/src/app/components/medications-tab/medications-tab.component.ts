import { ChevronRight } from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { Component, OnDestroy, OnInit, inject, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { PageActionService } from '../../services/page-action.service';
import { SearchResultItem } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { expiryBadge } from '../../shared/util/expiry';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { MedkitsPanelComponent } from '../medkits-panel/medkits-panel.component';

@Component({
    changeDetection: ChangeDetectionStrategy.Eager,
    selector: 'app-medications-tab',
    imports: [IconComponent, MedkitsPanelComponent, LoadingSpinnerComponent, RouterLink],
    templateUrl: './medications-tab.component.html',
  styles: [`
    .mt { display: flex; flex-direction: column; gap: var(--space-5); }
    .mt-title { margin: 0; font: var(--text-display); letter-spacing: var(--letter-spacing-display); }
    .mt-family { display: flex; flex-direction: column; gap: var(--space-3); }
    .mt-family-title { margin: 0; font: var(--text-title); }
    .mt-note { margin: 0; font: var(--text-body-sm); color: var(--color-text-muted); }
    .mt-row { padding: var(--space-3) var(--space-6); }
    .mt-row + .mt-row { border-top: 1px solid var(--color-divider); }
    @media (max-width: 1023px) { .mt-title { font: var(--text-display-mobile); letter-spacing: var(--letter-spacing-display); } }
    @media (max-width: 639px) { .mt-row { padding: var(--space-3) var(--space-4); } }
  `],
})
export class MedicationsTabComponent implements OnInit, OnDestroy {
  protected readonly chevron = ChevronRight;
  protected readonly formatDayMonthYear = formatDayMonthYear;
  readonly state = inject(FamilyStateService);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly pageAction = inject(PageActionService);

  /** Поиск по всем аптечкам всех семей (types=medication) — серверный, морфология + опечатки OCR. */
  readonly search = new DebouncedSearch<SearchResultItem>(
    (q) => this.api.search(q, 'medication').then((r) => r.items),
    (err) => (err instanceof ApiError ? err.message : 'Не удалось выполнить поиск.'),
  );

  readonly expiryBadge = expiryBadge;

  private paramsSub?: Subscription;

  ngOnInit(): void {
    // Редизайн v2.2 — раньше ?medkitId= из результата поиска (Главная/app-search/эта же
    // страница) двигал expandMedkitId, который medkits-panel ловил эффектом и раскрывал
    // аккордеон. Строка аптечки теперь ведёт на отдельную страницу (linkMode на
    // app-medkits-panel ниже), аккордеона нет — тот же query-параметр просто перенаправляет на
    // неё. Один переход обслуживает все три источника ссылки (Главная, поиск в шапке, поиск на
    // этой же вкладке — см. openResult) без изменений в них самих.
    this.paramsSub = this.route.queryParamMap.subscribe((params) => {
      const medkitId = params.get('medkitId');
      if (medkitId) void this.router.navigate(['/health/medications', medkitId], { replaceUrl: true });
    });
    // Редизайн v2.1 — поле поиска переехало в топбар каркаса целиком (было своим полем прямо на
    // экране, ниже заголовка «Аптечка» — та же жалоба, что на «Анализах»), см.
    // PageActionService.pageSearch. Результаты по-прежнему рендерятся здесь же, под сеткой аптечек.
    this.pageAction.setPageSearch({
      placeholder: 'Поиск по всем аптечкам…',
      value: () => this.search.query,
      onChange: (v) => this.onQueryChange(v),
    });
  }

  ngOnDestroy(): void {
    this.paramsSub?.unsubscribe();
    this.pageAction.clear();
  }

  onQueryChange(value: string): void {
    this.search.query = value;
    this.search.onQueryChange();
  }

  openResult(item: SearchResultItem): void {
    if (!item.medication) return;
    this.search.reset();
    void this.router.navigate(['/health/medications', item.medication.medkitId]);
  }
}
