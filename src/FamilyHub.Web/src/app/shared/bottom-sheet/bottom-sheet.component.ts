import { Component, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';

/**
 * Шторка снизу — по структуре зеркалит ModalComponent (open/closed, проекция контента),
 * но привязана к нижнему краю экрана (см. мокап «Доступ» в дизайн-дэке). Используется для
 * управления доступом к анализам; переиспользуема для прочих не-модальных настроек.
 *
 * Открытие шторки не меняет роут (это просто @Input() open), поэтому системная «назад»
 * (браузер/мобильный жест) её не видела вовсе — попадала на предыдущий экран истории целиком,
 * будто шторки не было. Синхронизация с History API здесь чинит это для ВСЕХ мест, где шторка
 * используется разом (Справка по медикаменту, карточка справочника, детали записи анализа):
 * открытие добавляет одну фиктивную запись в историю, «назад» её просто снимает и закрывает
 * шторку, оставляя пользователя на том экране, откуда он её открыл.
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

  /** Эта шторка (а не какая-то другая, если их несколько на странице) сама добавила запись
   * в историю — только тогда её и нужно снимать при закрытии не через «назад». */
  private pushedHistoryEntry = false;

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['open']) return;

    if (this.open && !this.pushedHistoryEntry) {
      history.pushState({ bottomSheet: true }, '', window.location.href);
      this.pushedHistoryEntry = true;
    } else if (!this.open && this.pushedHistoryEntry) {
      // Закрыли не «назад» (крестик/бэкдроп/Escape) — саму фиктивную запись нужно убрать,
      // иначе следующее нажатие «назад» схлопнет её вникуда, а не уведёт на предыдущий экран.
      this.pushedHistoryEntry = false;
      history.back();
    }
  }

  ngOnDestroy(): void {
    if (this.pushedHistoryEntry) {
      this.pushedHistoryEntry = false;
      history.back();
    }
  }

  @HostListener('window:popstate')
  onPopState(): void {
    if (this.open && this.pushedHistoryEntry) {
      this.pushedHistoryEntry = false;
      this.closed.emit();
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open) this.closed.emit();
  }

  requestClose(): void {
    this.closed.emit();
  }
}
