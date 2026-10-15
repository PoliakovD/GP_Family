import { Component, computed, input, ChangeDetectionStrategy } from '@angular/core';
import { ArrowDown, ArrowUp, TriangleAlert, type IconNode as LucideIconData } from 'lucide';
import { IndicatorFlag } from '../../models/types';
import { IconComponent } from '../ui/icon/icon.component';

/**
 * Бейдж статуса показателя (DESIGN §3 «Бейдж», «Анализ»): вне нормы — danger со стрелкой,
 * норма — success, без нормы — neutral. Цвет — только по смыслу (DESIGN §1 п.2: warning у анализов
 * не используется).
 *
 * `amount` — насколько значение вышло за границу («4», «2,26»): бейдж читается «↑ выше на 4», как в
 * макете analysis-desktop; без него — «↑ выше нормы». `Unknown` по умолчанию не рисуется (так ведут
 * себя списки и справка), а с `showUnknown` — нейтральный «без нормы» (строка таблицы показателей).
 */
@Component({
  changeDetection: ChangeDetectionStrategy.Eager,
  selector: 'app-status-chip',
  standalone: true,
  imports: [IconComponent],
  template: `
    @if (label(); as text) {
      <span class="badge status-chip" [class]="chipClass()">
        @if (icon(); as ic) {
          <app-icon [icon]="ic" [size]="14" [strokeWidth]="2.5" />
        }
        {{ text }}
      </span>
    }
  `,
})
export class StatusChipComponent {
  /** IndicatorFlag — см. FamilyHub.Domain.Enums.IndicatorFlag. */
  readonly flag = input.required<number>();
  /** Величина отклонения от границы нормы, уже отформатированная («0,59»). */
  readonly amount = input<string | null>(null);
  readonly showUnknown = input(false);

  readonly chipClass = computed(() => {
    switch (this.flag()) {
      case IndicatorFlag.Low:
      case IndicatorFlag.High:
      case IndicatorFlag.Critical:
        return 'badge-danger';
      case IndicatorFlag.Normal:
        return 'badge-success';
      default:
        return 'badge-neutral';
    }
  });

  readonly icon = computed<LucideIconData | null>(() => {
    switch (this.flag()) {
      case IndicatorFlag.Low:
        return ArrowDown;
      case IndicatorFlag.High:
        return ArrowUp;
      case IndicatorFlag.Critical:
        return TriangleAlert;
      default:
        return null;
    }
  });

  readonly label = computed(() => {
    const amount = this.amount();
    switch (this.flag()) {
      case IndicatorFlag.Low:
        return amount ? `ниже на ${amount}` : 'ниже нормы';
      case IndicatorFlag.High:
        return amount ? `выше на ${amount}` : 'выше нормы';
      case IndicatorFlag.Critical:
        return 'критично';
      case IndicatorFlag.Normal:
        return 'норма';
      default:
        return this.showUnknown() ? 'без нормы' : '';
    }
  });
}
