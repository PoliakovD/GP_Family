import {
  Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output,
  SimpleChanges, ViewChild,
} from '@angular/core';
import { HistoryDismissController } from '../util/history-dismiss';
import { OverlayA11y } from '../util/overlay-a11y';

/**
 * Обобщение `indicator-info-panel` (см. её docstring) на произвольную ширину — структурная
 * обёртка по таксономии `.claude/patterns/frontend_web.md` §«Pages / Panels / Modals-Toast»:
 * `@Input open`, `@Output closed`, `<ng-content>`, состоянием владеет консьюмер. Заведена для
 * админки (карточка задачи/справочника шире 392px индикаторной панели), но не специфична ей —
 * `indicator-info-panel` НЕ переведена на эту обёртку задним числом (нулевая функциональная
 * польза, см. правило «не переименовывать существующее под новую таксономию»).
 *
 * В отличие от `indicator-info-panel`, сразу заводит фокус-трап и блокировку скролла фона
 * (`OverlayA11y`) и синхронизацию с History API (`HistoryDismissController`, аппаратная/жестовая
 * «назад» закрывает панель, не уводит с текущего экрана) — тот же приём, что `file-viewer`
 * (модальные размеры `narrow`/`medium`) и `bottom-sheet`, которого не было у более старой
 * `indicator-info-panel` (исторический долг, не тронутый там намеренно).
 */
@Component({
  selector: 'app-side-panel',
  standalone: true,
  template: `
    <div class="overlay-backdrop side-panel-backdrop" (click)="closed.emit()">
      <aside
        #overlayRoot
        class="side-panel"
        [style.width]="width"
        tabindex="-1"
        (click)="$event.stopPropagation()"
      >
        <div class="side-panel-header">
          <h4 class="mb-0">{{ title }}</h4>
          <button type="button" class="btn-icon" aria-label="Закрыть" (click)="closed.emit()">
            <i class="ph ph-x" aria-hidden="true"></i>
          </button>
        </div>
        <div class="side-panel-body">
          <ng-content />
        </div>
      </aside>
    </div>
  `,
  styleUrl: './side-panel.component.scss',
})
export class SidePanelComponent implements OnChanges, OnDestroy {
  @Input() open = false;
  @Input() title = '';
  /** Любое валидное CSS-значение width — карточке задачи/справочника нужно больше, чем 392px
   * индикаторной панели. */
  @Input() width = 'min(560px, 100vw)';
  @Output() readonly closed = new EventEmitter<void>();

  @ViewChild('overlayRoot') private overlayRoot?: ElementRef<HTMLElement>;

  private readonly history = new HistoryDismissController(() => this.closed.emit());
  private readonly a11y = new OverlayA11y();

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['open']) return;

    this.history.sync(this.open);
    if (this.open) {
      queueMicrotask(() => this.overlayRoot && this.a11y.activate(this.overlayRoot.nativeElement));
    } else {
      this.a11y.deactivate();
    }
  }

  ngOnDestroy(): void {
    this.history.destroy();
    this.a11y.deactivate();
  }

  @HostListener('window:popstate')
  onPopState(): void {
    this.history.onPopState(this.open);
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (!this.open) return;
    if (event.key === 'Escape') {
      this.closed.emit();
    } else if (event.key === 'Tab' && this.overlayRoot) {
      this.a11y.trapTab(event, this.overlayRoot.nativeElement);
    }
  }
}
