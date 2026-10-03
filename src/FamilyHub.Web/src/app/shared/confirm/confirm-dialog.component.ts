import { Component, ElementRef, HostListener, ViewChild, effect, inject } from '@angular/core';
import { ConfirmService } from './confirm.service';
import { OverlayA11y } from '../util/overlay-a11y';
import { OverlayStackService } from '../util/overlay-stack.service';

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  templateUrl: './confirm-dialog.component.html',
  styleUrl: './confirm-dialog.component.scss',
})
export class ConfirmDialogComponent {
  readonly confirm = inject(ConfirmService);
  private readonly overlays = inject(OverlayStackService);
  private readonly a11y = new OverlayA11y();

  @ViewChild('card') private card?: ElementRef<HTMLElement>;

  constructor() {
    // Confirm открывается поверх чего угодно — регистрируемся в стеке, чтобы Escape закрыл только
    // его, а не заодно форму под ним. Фокус уходит внутрь: раньше он оставался на кнопке,
    // открывшей диалог, и Enter открывал confirm повторно.
    effect(() => {
      if (this.confirm.request()) {
        this.overlays.open(this);
        queueMicrotask(() => {
          if (!this.card) return;
          this.a11y.activate(this.card.nativeElement);
          // Для опасных действий фокус — на «Отмена», для обычных — на подтверждении.
          queueMicrotask(() => this.card?.nativeElement.querySelector<HTMLElement>('[data-autofocus]')?.focus());
        });
      } else {
        this.overlays.close(this);
        this.a11y.deactivate();
      }
    });
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: KeyboardEvent): void {
    if (this.confirm.request() && this.overlays.claimEscape(this, event)) this.confirm.resolve(false);
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Tab' && this.card) this.a11y.trapTab(event, this.card.nativeElement);
  }
}
