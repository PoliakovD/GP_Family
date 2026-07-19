import { Component, EventEmitter, HostListener, Input, Output } from '@angular/core';

/**
 * Шторка снизу — по структуре зеркалит ModalComponent (open/closed, проекция контента),
 * но привязана к нижнему краю экрана (см. мокап «Доступ» в дизайн-дэке). Используется для
 * управления доступом к анализам; переиспользуема для прочих не-модальных настроек.
 */
@Component({
  selector: 'app-bottom-sheet',
  standalone: true,
  templateUrl: './bottom-sheet.component.html',
  styleUrl: './bottom-sheet.component.scss',
})
export class BottomSheetComponent {
  @Input() title = '';
  @Input() open = false;
  @Output() closed = new EventEmitter<void>();

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open) this.closed.emit();
  }

  requestClose(): void {
    this.closed.emit();
  }
}
