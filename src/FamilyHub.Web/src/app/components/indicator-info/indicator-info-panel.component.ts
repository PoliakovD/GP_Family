import { Component, EventEmitter, Input, Output } from '@angular/core';

/**
 * Обёртка боковой панели справки (десктоп, `tier()==='wide'`) — редизайн v2, PR4. Тот же приём
 * двух обёрток на одном содержимом (`<app-indicator-info>`), что и `shared/action-menu`: узкие
 * экраны монтируют то же тело в `shared/bottom-sheet` напрямую, отдельной обёртки не требуется.
 * Простой fixed-оверлей с фоновой подложкой (как `shared/modal`), а не часть постоянной
 * раскладки — так проще гарантировать, что панель не перекрывает управление сайдбаром/топбаром
 * и одинаково работает и в `medical-records-panel`, и в `kb-analyte-tab`.
 */
@Component({
  selector: 'app-indicator-info-panel',
  standalone: true,
  template: `
    <div class="overlay-backdrop indicator-info-backdrop" (click)="closed.emit()">
      <aside class="indicator-info-panel" (click)="$event.stopPropagation()">
        <div class="indicator-info-panel-header">
          <h4 class="mb-0">{{ title }}</h4>
          <button type="button" class="btn-icon" aria-label="Закрыть" (click)="closed.emit()">
            <i class="ph ph-x" aria-hidden="true"></i>
          </button>
        </div>
        <div class="indicator-info-panel-body">
          <ng-content />
        </div>
      </aside>
    </div>
  `,
  styleUrl: './indicator-info-panel.component.scss',
})
export class IndicatorInfoPanelComponent {
  @Input() title = 'Справка';
  @Output() readonly closed = new EventEmitter<void>();
}
