import { Component, EventEmitter, Input, Output } from '@angular/core';
import { X } from 'lucide-angular';
import { IconComponent } from '../../shared/ui/icon/icon.component';

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
  imports: [IconComponent],
  template: `
    <div class="overlay-backdrop indicator-info-backdrop" (click)="closed.emit()">
      <aside class="indicator-info-panel" (click)="$event.stopPropagation()">
        <div class="indicator-info-panel-header">
          <div class="indicator-info-panel-heading">
            <div class="indicator-info-panel-kicker">Справочник FamilyHub</div>
            <h2 class="indicator-info-panel-title">{{ title }}</h2>
          </div>
          <button type="button" class="btn-icon" aria-label="Закрыть" (click)="closed.emit()">
            <app-icon [icon]="closeIcon" />
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
  protected readonly closeIcon = X;
  @Input() title = 'Справка';
  @Output() readonly closed = new EventEmitter<void>();
}
