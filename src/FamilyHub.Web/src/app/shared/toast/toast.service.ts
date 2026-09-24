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
  info: 4000,
  error: 6000,
};

/** Тост с действием живёт дольше обычного — «Отменить» нужно успеть заметить и нажать. */
const UNDO_DURATION_MS = 8000;

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
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  /** Вызывает действие тоста и закрывает его: повторный клик по уже выполненной отмене
   * не должен запускать её второй раз. */
  async runAction(toast: Toast): Promise<void> {
    this.dismiss(toast.id);
    await toast.action?.run();
  }

  private show(type: ToastType, text: string, options?: ToastOptions): void {
    const id = this.nextId++;
    this.toasts.update((list) => [...list, { id, type, text, action: options?.action }]);
    setTimeout(() => this.dismiss(id), options?.durationMs ?? DURATIONS[type]);
  }
}
