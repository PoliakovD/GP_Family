import { Component, input } from '@angular/core';
import { IndicatorFlag } from '../../models/types';

/**
 * Чип статуса показателя (норма/выше/ниже/критично) — редизайн v2. Извлечён из
 * `MedicalRecordsPanelComponent.flagClass()`/`flagLabel()` (см. `.claude/plans/...` PR1) как
 * первый переиспользуемый потребитель наравне с таблицей показателей десктопа/карточками
 * мобайла/панелью справки (PR4). Цвета — исключительно из канонических `--color-status-*`
 * (styles.scss), которые сами канонизированы из аптечной палитры сроков годности
 * (medications-panel.component.scss) — не новые смыслы.
 *
 * `Unknown` (нет референса/значения) рендерится пустым — так же вело себя старое `flagLabel()`
 * (пустая строка): такие показатели в UI сворачиваются в отдельную "N без нормы"-строку, а не
 * показываются построчно с пустым чипом.
 */
@Component({
  selector: 'app-status-chip',
  standalone: true,
  template: `
    @if (label(); as text) {
      <span class="status-chip" [class]="chipClass()">
        @if (icon(); as ic) {
          <i [class]="ic" aria-hidden="true"></i>
        }
        {{ text }}
      </span>
    }
  `,
  styleUrl: './status-chip.component.scss',
})
export class StatusChipComponent {
  /** IndicatorFlag — см. FamilyHub.Domain.Enums.IndicatorFlag. */
  readonly flag = input.required<number>();

  chipClass(): string {
    switch (this.flag()) {
      case IndicatorFlag.Low:
      case IndicatorFlag.High:
        return 'status-chip-warning';
      case IndicatorFlag.Critical:
        return 'status-chip-danger';
      case IndicatorFlag.Normal:
        return 'status-chip-ok';
      default:
        return 'status-chip-unknown';
    }
  }

  icon(): string | null {
    switch (this.flag()) {
      case IndicatorFlag.Low:
        return 'ph-fill ph-arrow-down';
      case IndicatorFlag.High:
        return 'ph-fill ph-arrow-up';
      case IndicatorFlag.Critical:
        return 'ph-fill ph-warning';
      case IndicatorFlag.Normal:
        return 'ph-fill ph-check';
      default:
        return null;
    }
  }

  label(): string {
    switch (this.flag()) {
      case IndicatorFlag.Low:
        return 'ниже нормы';
      case IndicatorFlag.High:
        return 'выше нормы';
      case IndicatorFlag.Critical:
        return 'критично';
      case IndicatorFlag.Normal:
        return 'норма';
      default:
        return '';
    }
  }
}
