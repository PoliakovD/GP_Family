import { Component, ElementRef, HostListener, inject, input, signal } from '@angular/core';
import { BreakpointService } from '../../services/breakpoint.service';
import { BottomSheetComponent } from '../bottom-sheet/bottom-sheet.component';

/** Один пункт меню «…» — редизайн v2. Не более того, что уже умеют существующие
 * `.btn`/иконки Phosphor — специально не вводится отдельная "опасное действие" разметка сверх
 * `danger`, чтобы не плодить третий визуальный язык кнопок. */
export interface ActionMenuItem {
  label: string;
  icon?: string; // класс Phosphor-иконки
  danger?: boolean;
  handler: () => void;
}

/**
 * Меню «…» (редизайн v2) — на широких экранах лёгкий popover с закрытием по клику вне/Escape,
 * на узких — существующий `shared/bottom-sheet` (уже умеет popstate/Escape сам). Первый
 * потребитель — «Удалить семью» в `family-details` (PR3c); дальше переиспользуется в карточке
 * записи «Анализов» (PR3b) и в статье показателя (PR4) — тот же приём двух обёрток на одном
 * наборе действий, что и `indicator-info`/`indicator-info-panel`.
 */
@Component({
  selector: 'app-action-menu',
  standalone: true,
  imports: [BottomSheetComponent],
  templateUrl: './action-menu.component.html',
  styleUrl: './action-menu.component.scss',
})
export class ActionMenuComponent {
  private readonly breakpoints = inject(BreakpointService);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly actions = input.required<ActionMenuItem[]>();
  /** aria-label кнопки-триггера и заголовок bottom-sheet на узких экранах. */
  readonly label = input('Ещё действия');

  readonly open = signal(false);

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  select(item: ActionMenuItem): void {
    this.open.set(false);
    item.handler();
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (!this.open() || !this.isWide) return; // мобильная шторка закрывается сама (popstate/Escape/бэкдроп)
    if (!this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open() && this.isWide) this.open.set(false);
  }
}
