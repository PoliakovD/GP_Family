import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { VaccinationStateService } from '../../services/vaccination-state.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SidePanelComponent } from '../../shared/side-panel/side-panel.component';
import { GROUP_LABEL } from '../../shared/util/vaccination-labels';

/**
 * Справочник «Прививки» (ADR-0016): статические карточки каталога, без ИИ-обогащения — закрытое
 * множество серий, конвейер справочника препаратов/анализов тут не нужен. `?id=` открывает
 * конкретную серию (та же точка входа, что у kb-analyte-tab — «О вакцине в справочнике»).
 */
@Component({
  selector: 'app-kb-vaccines-tab',
  imports: [BottomSheetComponent, FormsModule, LoadingSpinnerComponent, NgTemplateOutlet, SidePanelComponent],
  templateUrl: './kb-vaccines-tab.component.html',
  styleUrl: './kb-vaccines-tab.component.scss',
})
export class KbVaccinesTabComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointService);
  protected readonly vaccinations = inject(VaccinationStateService);
  private queryParamsSub?: Subscription;

  protected readonly groupLabel = GROUP_LABEL;
  protected readonly query = signal('');
  protected readonly selectedCode = signal<string | null>(null);
  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');

  protected readonly items = computed(() => {
    const q = this.query().trim().toLowerCase();
    const all = this.vaccinations.catalog() ?? [];
    if (!q) return all;
    return all.filter((s) => s.name.toLowerCase().includes(q) || s.diseases.some((d) => d.toLowerCase().includes(q))
      || s.tradeNames.some((t) => t.toLowerCase().includes(q)));
  });

  protected readonly selected = computed(() => (this.vaccinations.catalog() ?? []).find((s) => s.code === this.selectedCode()) ?? null);

  async ngOnInit(): Promise<void> {
    await this.vaccinations.ensureCatalog();
    this.queryParamsSub = this.route.queryParamMap.subscribe((params) => {
      const id = params.get('id');
      if (id) this.selectedCode.set(id);
    });
  }

  ngOnDestroy(): void {
    this.queryParamsSub?.unsubscribe();
  }

  protected open(code: string): void {
    this.selectedCode.set(code);
  }

  protected close(): void {
    this.selectedCode.set(null);
    void this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }
}
