import { Component, OnInit, computed, effect, inject, untracked } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { BreakpointService } from '../../services/breakpoint.service';
import { VaccinationStateService } from '../../services/vaccination-state.service';
import { PageActionService } from '../../services/page-action.service';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SidePanelComponent } from '../../shared/side-panel/side-panel.component';
import { VaccinationAddModalComponent, VaccinationSavedEvent } from '../vaccination-add-modal/vaccination-add-modal.component';
import { VaccinationSavedSheetComponent } from '../vaccination-saved-sheet/vaccination-saved-sheet.component';

/**
 * Страница-хаб «Прививки» (макет «Screen - Vaccination»): сама рисует только вложенные роуты
 * (обзор / график человека) — общие панели (модалка «Добавить», шторка «Сохранено») открывают из
 * разных экранов через VaccinationStateService, а рисует одно место, как IntakePageComponent.
 */
@Component({
  selector: 'app-vaccinations-page',
  imports: [
    BottomSheetComponent, RouterOutlet, SidePanelComponent, VaccinationAddModalComponent,
    VaccinationSavedSheetComponent,
  ],
  templateUrl: './vaccinations-page.component.html',
  styleUrl: './vaccinations-page.component.scss',
})
export class VaccinationsPageComponent implements OnInit {
  private readonly breakpoints = inject(BreakpointService);
  protected readonly vaccinations = inject(VaccinationStateService);
  protected readonly pageAction = inject(PageActionService);

  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');

  constructor() {
    effect(() => {
      this.vaccinations.version();
      untracked(() => void this.vaccinations.refresh());
    });
  }

  ngOnInit(): void {
    void this.vaccinations.refresh();
  }

  protected newVaccination(): void {
    this.vaccinations.openAdd();
  }

  protected onAdded(event: VaccinationSavedEvent): void {
    this.vaccinations.closeAdd();
    this.vaccinations.changed();
    if (event.info) this.vaccinations.showSaved(event.info);
  }
}
