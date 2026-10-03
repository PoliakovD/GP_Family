import { Injectable } from '@angular/core';

/**
 * Порядок открытых оверлеев (модалка, confirm, нижний лист, боковая панель, просмотр файла) —
 * чтобы Escape закрывал только ВЕРХНИЙ. Раньше каждый слушал `document:keydown.escape` сам по
 * себе: confirm «Удалить?» поверх формы → один Escape закрывал и confirm, и форму с введёнными
 * данными.
 *
 * Использование: `open(this)` при открытии, `close(this)` при закрытии/уничтожении, в обработчике
 * Escape — `if (!stack.claimEscape(this, event)) return;`.
 */
@Injectable({ providedIn: 'root' })
export class OverlayStackService {
  private readonly stack: object[] = [];

  open(owner: object): void {
    this.close(owner);
    this.stack.push(owner);
  }

  close(owner: object): void {
    const i = this.stack.indexOf(owner);
    if (i >= 0) this.stack.splice(i, 1);
  }

  hasOpen(): boolean {
    return this.stack.length > 0;
  }

  /**
   * true — этот оверлей верхний и забирает Escape себе (помечает событие обработанным, чтобы
   * остальные слушатели того же нажатия его проигнорировали, даже если верхний успел закрыться
   * синхронно и стек уже сдвинулся).
   */
  claimEscape(owner: object, event: Event | undefined): boolean {
    if (event?.defaultPrevented) return false;
    if (this.stack[this.stack.length - 1] !== owner) return false;
    event?.preventDefault();
    return true;
  }
}
