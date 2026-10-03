import { Directive, ElementRef, HostBinding, HostListener, inject, input } from '@angular/core';

/**
 * Кликабельная карточка/строка (div, tr), которую нельзя сделать <button> из-за вёрстки:
 * роль кнопки, фокус с клавиатуры и активация по Enter/Space. Раньше такие элементы были
 * недоступны без мыши и не объявлялись скринридеру как интерактивные.
 *
 *   <div class="card" appClickable (click)="open()">…</div>
 *   <div class="card" [appClickable]="isNavigable(item)" (click)="open(item)">…</div>
 */
@Directive({
  selector: '[appClickable]',
  standalone: true,
})
export class ClickableDirective {
  /** false — элемент не интерактивен (например, у результата поиска нет экрана). '' = true. */
  readonly appClickable = input<boolean | ''>(true);

  private readonly host = inject(ElementRef<HTMLElement>);

  private get enabled(): boolean {
    return this.appClickable() !== false;
  }

  @HostBinding('attr.role') get role(): string | null {
    return this.enabled ? 'button' : null;
  }

  @HostBinding('attr.tabindex') get tabindex(): number | null {
    return this.enabled ? 0 : null;
  }

  @HostListener('keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    if (!this.enabled || event.target !== this.host.nativeElement) return;
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      this.host.nativeElement.click();
    }
  }
}
