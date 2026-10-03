import {
  Component, ElementRef, EventEmitter, HostListener, Input, OnChanges, OnDestroy, Output, SimpleChanges, ViewChild,
  inject,
} from '@angular/core';
import { OverlayA11y } from '../util/overlay-a11y';
import { OverlayStackService } from '../util/overlay-stack.service';

let nextModalId = 0;

@Component({
  selector: 'app-modal',
  standalone: true,
  templateUrl: './modal.component.html',
  styleUrl: './modal.component.scss',
})
export class ModalComponent implements OnChanges, OnDestroy {
  @Input() title = '';
  @Input() open = false;
  /** Широкая карточка (списки с превью текста) — по умолчанию оверлей узкий (400px), под формы. */
  @Input() wide = false;
  /** Форма внутри модалки «грязная» — тап мимо карточки тогда не закрывает её (раньше одно
   * случайное касание фона стирало введённое). Крестик и Escape закрывают как обычно. */
  @Input() dirty = false;
  @Output() closed = new EventEmitter<void>();

  @ViewChild('card') private card?: ElementRef<HTMLElement>;

  readonly titleId = `modal-title-${nextModalId++}`;
  private readonly overlays = inject(OverlayStackService);
  private readonly a11y = new OverlayA11y();

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['open']) return;
    if (this.open) {
      this.overlays.open(this);
      queueMicrotask(() => this.card && this.a11y.activate(this.card.nativeElement));
    } else {
      this.overlays.close(this);
      this.a11y.deactivate();
    }
  }

  ngOnDestroy(): void {
    this.overlays.close(this);
    this.a11y.deactivate();
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: KeyboardEvent): void {
    if (this.open && this.overlays.claimEscape(this, event)) this.closed.emit();
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Tab' && this.card) this.a11y.trapTab(event, this.card.nativeElement);
  }

  onBackdropClick(): void {
    if (!this.dirty) this.closed.emit();
  }

  requestClose(): void {
    this.closed.emit();
  }
}
