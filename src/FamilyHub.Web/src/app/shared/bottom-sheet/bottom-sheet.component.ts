import { Component, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output, SimpleChanges, inject } from '@angular/core';
import { HistoryDismissController } from '../util/history-dismiss';
import { OverlayStackService } from '../util/overlay-stack.service';

/**
 * Шторка снизу — по структуре зеркалит ModalComponent (open/closed, проекция контента),
 * но привязана к нижнему краю экрана (см. мокап «Доступ» в дизайн-дэке). Используется для
 * управления доступом к анализам; переиспользуема для прочих не-модальных настроек.
 *
 * Синхронизация с History API (см. shared/util/history-dismiss.ts, тот же приём использует
 * shared/file-viewer) чинит системную «назад» для ВСЕХ мест, где шторка используется разом
 * (Справка по медикаменту, карточка справочника, детали записи анализа).
 */
@Component({
  selector: 'app-bottom-sheet',
  standalone: true,
  templateUrl: './bottom-sheet.component.html',
  styleUrl: './bottom-sheet.component.scss',
})
export class BottomSheetComponent implements OnChanges, OnDestroy {
  @Input() title = '';
  @Input() open = false;
  @Output() closed = new EventEmitter<void>();

  private readonly history = new HistoryDismissController(() => this.closed.emit());
  private readonly overlays = inject(OverlayStackService);

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['open']) {
      this.history.sync(this.open);
      if (this.open) this.overlays.open(this); else this.overlays.close(this);
    }
  }

  ngOnDestroy(): void {
    this.history.destroy();
    this.overlays.close(this);
  }

  @HostListener('window:popstate')
  onPopState(): void {
    this.history.onPopState(this.open);
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: KeyboardEvent): void {
    if (this.open && this.overlays.claimEscape(this, event)) this.closed.emit();
  }

  requestClose(): void {
    this.closed.emit();
  }
}
