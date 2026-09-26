import { Component, OnInit, effect, inject, untracked } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { IntakeStateService } from '../../services/intake-state.service';
import { PageActionService } from '../../services/page-action.service';

/**
 * Страница «Приём лекарств» (подпункт «Здоровья» рядом с «Аптечкой», макет «Screen - Medication schedule»):
 * переключатель «Сегодня / Курсы» и общие панели (форма курса, настройки напоминаний) — их открывают из
 * разных экранов через IntakeStateService, а рисует одно место. Дочерние экраны — вложенные роуты, поэтому
 * переживают refresh и работают с «назад».
 */
@Component({
  selector: 'app-intake-page',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './intake-page.component.html',
  styleUrl: './intake-page.component.scss',
})
export class IntakePageComponent implements OnInit {
  protected readonly intake = inject(IntakeStateService);
  protected readonly pageAction = inject(PageActionService);

  constructor() {
    // Счётчик «Курсы · N» и список обновляются после любого изменения.
    effect(() => {
      this.intake.version();
      untracked(() => void this.intake.loadCourses());
    });
  }

  ngOnInit(): void {
    void this.intake.refresh();
  }

  protected newCourse(): void {
    this.intake.openForm();
  }
}
