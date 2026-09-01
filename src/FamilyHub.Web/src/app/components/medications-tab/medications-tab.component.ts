import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiService, ApiError } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import { PageActionService } from '../../services/page-action.service';
import { SearchResultItem } from '../../models/types';
import { DebouncedSearch } from '../../shared/util/debounced-search';
import { expiryClass } from '../../shared/util/expiry';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { MedkitsPanelComponent } from '../medkits-panel/medkits-panel.component';

@Component({
  selector: 'app-medications-tab',
  standalone: true,
  imports: [MedkitsPanelComponent, SearchFieldComponent, LoadingSpinnerComponent],
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

  // Куда автораскрыть аптечку при переходе с результата поиска (Главная или клик по своему же
  // результату) — query-параметры, не in-page state: переживают refresh, работают с browser back.
  readonly expandFamilyId = signal<string | null>(null);
  readonly expandMedkitId = signal<string | null>(null);

  readonly expiryClass = expiryClass;

  private paramsSub?: Subscription;

  ngOnInit(): void {
    this.paramsSub = this.route.queryParamMap.subscribe((params) => {
      this.expandFamilyId.set(params.get('familyId'));
      this.expandMedkitId.set(params.get('medkitId'));
    });
    // Редизайн v3 — «один поиск на экране»: своё поле поиска выше уже покрывает все аптечки
    // всех семей, общий поиск шапки на этом экране только дублировал бы его.
    this.pageAction.setSearchSuppressed(true);
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
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { familyId: item.medication.familyId, medkitId: item.medication.medkitId },
      queryParamsHandling: 'merge',
    });
  }
}
