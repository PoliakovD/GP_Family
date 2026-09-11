import {
  Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output,
  SimpleChanges, ViewChild,
} from '@angular/core';
import { OverlayA11y } from '../util/overlay-a11y';

/**
 * Обобщение `indicator-info-panel` (см. её docstring) на произвольную ширину — структурная
 * обёртка по таксономии `.claude/patterns/frontend_web.md` §«Pages / Panels / Modals-Toast»:
 * `@Input open`, `@Output closed`, `<ng-content>`, состоянием владеет консьюмер. Заведена для
 * админки (карточка задачи/справочника шире 392px индикаторной панели), но не специфична ей —
 * `indicator-info-panel` НЕ переведена на эту обёртку задним числом (нулевая функциональная
 * польза, см. правило «не переименовывать существующее под новую таксономию»).
 *
 * Заводит фокус-трап и блокировку скролла фона (`OverlayA11y`), но НЕ `HistoryDismissController`
 * (в отличие от `file-viewer`/`bottom-sheet`) — тот трюк (push/pop фиктивной записи истории)
 * предполагает, что открытие/закрытие оверлея НИКАК не отражено в URL. Консьюмеры этой панели в
 * админке (карточка задачи/справочника) уже сами синхронизируют open/closed с query-параметром
 * (`?job=<id>` и т.п., см. AdminPipelineComponent.openJob/closeJobPanel) через собственный
 * `router.navigate(..., {replaceUrl: true})`. Если бы обе синхронизации (query-параметр И
 * push/pop истории) работали одновременно, `replaceUrl` от закрытия панели затирает state
 * фиктивной записи, `history.back()` из HistoryDismissController уводит на предыдущую запись,
 * где `?job=` ещё был, а Router восстанавливает эту query-строку — следующий `merge` навигации
 * возвращает `job` обратно, и панель никогда не закрывается по-настоящему (баг, найденный в
 * проде: «вечно висит открытая задача»). Урок: для оверлея, чьё состояние уже отражено в URL,
 * не заводить вторую, независимую систему адресации той же истории.
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

  private readonly a11y = new OverlayA11y();

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['open']) return;

    if (this.open) {
      queueMicrotask(() => this.overlayRoot && this.a11y.activate(this.overlayRoot.nativeElement));
    } else {
      this.a11y.deactivate();
    }
  }

  ngOnDestroy(): void {
    this.a11y.deactivate();
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
