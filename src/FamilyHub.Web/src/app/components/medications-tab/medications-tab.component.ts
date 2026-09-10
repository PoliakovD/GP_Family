import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { PageActionService } from '../../services/page-action.service';
import { SearchResultItem } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { expiryClass } from '../../shared/util/expiry';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { MedkitsPanelComponent } from '../medkits-panel/medkits-panel.component';

@Component({
  selector: 'app-medications-tab',
  standalone: true,
  imports: [MedkitsPanelComponent, LoadingSpinnerComponent],
  templateUrl: './medications-tab.component.html',
})
export class MedicationsTabComponent implements OnInit, OnDestroy {
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

  readonly expiryClass = expiryClass;

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
