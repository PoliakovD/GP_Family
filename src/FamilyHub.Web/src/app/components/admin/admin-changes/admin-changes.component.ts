import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { KbChangeTarget, KbChangeTargetValue } from '../../../services/admin-api.service';
import { KbHistoryComponent } from '../admin-review/kb-history.component';

const FILTERS: { value: KbChangeTargetValue | null; label: string }[] = [
  { value: null, label: 'Все' },
  { value: KbChangeTarget.LabAnalyteKb, label: 'Показатели' },
  { value: KbChangeTarget.MedicationKb, label: 'Препараты' },
  { value: KbChangeTarget.LabAnalyteSearchCache, label: 'Кэш (показатели)' },
  { value: KbChangeTarget.MedicationSearchCache, label: 'Кэш (препараты)' },
];

/**
 * «Операции → Журнал правок»: общий журнал изменений справочников и кэша поиска (KbChangeLog) — кто, когда,
 * что изменилось, с откатом. Раньше журнал был виден только из карточки одной статьи. Фильтр по типу — в
 * URL (?target=), чтобы ссылку можно было переслать.
 */
@Component({
  selector: 'app-admin-changes',
  imports: [KbHistoryComponent],
  template: `
    <div class="d-flex mb-3 justify-content-end">
      <div class="seg" role="group" aria-label="Тип записей">
        @for (f of filters; track f.label) {
          <button type="button" class="seg-opt" [class.active]="target() === f.value" (click)="select(f.value)">{{ f.label }}</button>
        }
      </div>
    </div>
    <div class="card">
      <div class="card-body">
        <app-kb-history title="Журнал правок справочников и кэша" [target]="target()" [targetId]="null" [showRecord]="true" />
      </div>
    </div>
  `,
  styles: [`
    .seg-opt { font: inherit; font-size: 0.7647rem; color: inherit; background: transparent; border: 0; }
    .seg-opt + .seg-opt { border-left: 1px solid var(--color-divider); }
    .seg-opt.active { color: var(--color-bg); background: var(--color-accent); }
  `],
})
export class AdminChangesComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly filters = FILTERS;
  readonly target = signal<KbChangeTargetValue | null>(this.parseTarget(this.route.snapshot.queryParamMap.get('target')));

  select(value: KbChangeTargetValue | null): void {
    this.target.set(value);
    void this.router.navigate([], {
      relativeTo: this.route, queryParams: { target: value }, queryParamsHandling: 'merge', replaceUrl: true,
    });
  }

  private parseTarget(raw: string | null): KbChangeTargetValue | null {
    const n = raw === null ? NaN : Number(raw);
    return FILTERS.some((f) => f.value === n) ? (n as KbChangeTargetValue) : null;
  }
}
