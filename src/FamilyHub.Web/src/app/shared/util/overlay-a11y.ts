// Ни shared/modal, ни shared/bottom-sheet сегодня не блокируют скролл фона и не держат фокус
// внутри себя (см. .claude/patterns/frontend_web.md / отчёт аудита UI) — не трогаем их в этом PR
// (отдельная задача), но новый оверлей (file-viewer) заводим сразу правильно: полноэкранный
// просмотрщик документов — не место, где предсказуемый Tab и вернувшийся фокус необязательны.

const FOCUSABLE_SELECTOR =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** Один экземпляр на компонент-оверлей. `activate()` из ngOnChanges при открытии,
 * `deactivate()` при закрытии/ngOnDestroy, `trapTab()` из (keydown.tab) на корневом элементе. */
export class OverlayA11y {
  private previouslyFocused: HTMLElement | null = null;
  private previousBodyOverflow = '';
  private active = false;

  activate(container: HTMLElement): void {
    if (this.active) return;
    this.active = true;
    this.previouslyFocused = document.activeElement as HTMLElement | null;
    this.previousBodyOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    // Микротаск — контейнер должен быть уже в DOM (Angular ещё не закончил текущий цикл рендера,
    // когда activate() вызывается синхронно из ngOnChanges).
    queueMicrotask(() => container.focus());
  }

  deactivate(): void {
    if (!this.active) return;
    this.active = false;
    document.body.style.overflow = this.previousBodyOverflow;
    this.previouslyFocused?.focus?.();
    this.previouslyFocused = null;
  }

  /** Вызывать на keydown.tab (и keydown.shift.tab — event.shiftKey уже несёт направление). */
  trapTab(event: KeyboardEvent, container: HTMLElement): void {
    const focusable = Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
    if (focusable.length === 0) {
      event.preventDefault();
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }
}
