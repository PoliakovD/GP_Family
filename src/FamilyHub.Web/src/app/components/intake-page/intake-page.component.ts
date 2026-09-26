import { Component, OnInit, computed, effect, inject, input, untracked } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { BreakpointService } from '../../services/breakpoint.service';
import { IntakeStateService } from '../../services/intake-state.service';
import { PageActionService } from '../../services/page-action.service';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SidePanelComponent } from '../../shared/side-panel/side-panel.component';
import { IntakeCourseFormComponent } from '../intake-course-form/intake-course-form.component';
import { IntakeRemindersComponent } from '../intake-reminders/intake-reminders.component';

/**
 * Страница «Приём лекарств» (подпункт «Здоровья» рядом с «Аптечкой», макет «Screen - Medication schedule»):
 * переключатель «Сегодня / Курсы» и общие панели (форма курса, настройки напоминаний) — их открывают из
 * разных экранов через IntakeStateService, а рисует одно место: боковая панель на десктопе, нижний лист на
 * мобильном. Дочерние экраны — вложенные роуты, поэтому переживают refresh и работают с «назад».
 * Query-параметры открывают форму сразу: `?new=1` — пустую, `?record=&idx=&dep=` — из назначения врача
 * (кнопка «Начать курс» в приёме врача).
 */
@Component({
  selector: 'app-intake-page',
  imports: [
    BottomSheetComponent, IntakeCourseFormComponent, IntakeRemindersComponent, RouterLink, RouterLinkActive,
    RouterOutlet, SidePanelComponent,
  ],
  templateUrl: './intake-page.component.html',
  styleUrl: './intake-page.component.scss',
})
export class IntakePageComponent implements OnInit {
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointService);
  protected readonly intake = inject(IntakeStateService);
  protected readonly pageAction = inject(PageActionService);

  // Привязка query-параметров (withComponentInputBinding).
  readonly record = input<string | undefined>(undefined);
  readonly idx = input<string | undefined>(undefined);
  readonly dep = input<string | undefined>(undefined);
  readonly create = input<string | undefined>(undefined, { alias: 'new' });

  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');
  protected readonly formTitle = computed(() => (this.intake.form()?.courseId ? 'Изменить курс' : 'Новый курс'));

  constructor() {
    // Счётчик «Курсы · N» и список обновляются после любого изменения.
    effect(() => {
      this.intake.version();
      untracked(() => void this.intake.loadCourses());
    });

    // Форма по ссылке: из приёма врача (record) или «новый курс» (new).
    effect(() => {
      const record = this.record();
      const create = this.create();
      if (!record && !create) return;
      untracked(() => {
        const index = Number(this.idx());
        this.intake.openForm(record
          ? { recordId: record, prescriptionIndex: Number.isFinite(index) ? index : undefined, dependentId: this.dep() ?? null }
          : {});
        void this.router.navigate([], {
          queryParams: { record: null, idx: null, dep: null, new: null },
          queryParamsHandling: 'merge',
          replaceUrl: true,
        });
      });
    });
  }

  ngOnInit(): void {
    void this.intake.refresh();
  }

  protected newCourse(): void {
    this.intake.openForm();
  }

  protected onFormSaved(): void {
    this.intake.closeForm();
    this.intake.changed();
  }
}
