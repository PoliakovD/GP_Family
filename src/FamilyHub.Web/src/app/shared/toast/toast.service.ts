import { Injectable, signal } from '@angular/core';

export type ToastType = 'success' | 'error' | 'info';

/** Кнопка внутри тоста (например, «Отменить») — клик по самой кнопке не закрывает тост через
 * обработчик клика по телу, см. toast-container.component.html. */
export interface ToastAction {
  label: string;
  run: () => void | Promise<void>;
}

export interface Toast {
  id: number;
  type: ToastType;
  text: string;
  action?: ToastAction;
}

export interface ToastOptions {
  action?: ToastAction;
  durationMs?: number;
}

const DURATIONS: Record<ToastType, number> = {
  success: 4000,
  info: 6000,
  error: 8000,
};

/** Длинный текст читается дольше: +50 мс на символ сверх 60, но не больше 15 с. */
function durationFor(type: ToastType, text: string): number {
  return Math.min(15_000, DURATIONS[type] + Math.max(0, text.length - 60) * 50);
}

/** Тост с действием живёт дольше обычного — «Отменить» нужно успеть заметить и нажать. */
const UNDO_DURATION_MS = 10_000;

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);

  private nextId = 1;

  success(text: string): void {
    this.show('success', text);
  }

  error(text: string): void {
    this.show('error', text);
  }

  info(text: string): void {
    this.show('info', text);
  }

  /** Успех с кнопкой «Отменить» — для сохранений «сразу по клику» (переключатели), где нет
   * отдельного шага подтверждения. undo сам решает, что значит «отменить» (обычно — записать
   * прежнее значение обратно). */
  successWithUndo(text: string, undo: () => void | Promise<void>): void {
    this.show('success', text, { action: { label: 'Отменить', run: undo }, durationMs: UNDO_DURATION_MS });
  }

  dismiss(id: number): void {
    const t = this.timers.get(id);
    if (t) clearTimeout(t.handle);
    this.timers.delete(id);
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  /** Вызывает действие тоста и закрывает его: повторный клик по уже выполненной отмене
   * не должен запускать её второй раз. */
  async runAction(toast: Toast): Promise<void> {
    this.dismiss(toast.id);
    await toast.action?.run();
  }

  private readonly timers = new Map<number, { handle: ReturnType<typeof setTimeout>; remaining: number; started: number }>();

  /** Пока палец/курсор на тосте или фокус на его кнопке — не исчезает (пожилым нужно время дочитать). */
  pause(id: number): void {
    const t = this.timers.get(id);
    if (!t) return;
    clearTimeout(t.handle);
    t.remaining -= Date.now() - t.started;
  }

  resume(id: number): void {
    const t = this.timers.get(id);
    if (!t) return;
    t.started = Date.now();
    t.handle = setTimeout(() => this.dismiss(id), Math.max(1500, t.remaining));
  }

  private show(type: ToastType, text: string, options?: ToastOptions): void {
    const id = this.nextId++;
    // Не больше трёх одновременно — старые уходят, чтобы стопка не закрывала экран.
    this.toasts.update((list) => [...list.slice(-2), { id, type, text, action: options?.action }]);
    const remaining = options?.durationMs ?? durationFor(type, text);
    this.timers.set(id, { handle: setTimeout(() => this.dismiss(id), remaining), remaining, started: Date.now() });
  }
}
