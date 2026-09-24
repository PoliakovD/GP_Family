import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { ConfirmService } from '../shared/confirm/confirm.service';

/** Страница админки с формой, изменения которой применяются только кнопкой «Сохранить». */
export interface HasUnsavedChanges {
  hasUnsavedChanges(): boolean;
}

/**
 * Не даёт молча потерять несохранённые правки при уходе с роута (переключение вкладки админки,
 * ссылка, кнопка «назад»). Состояние формы — сигналы внутри компонента, поэтому уход с роута
 * разрушает компонент вместе с ними.
 *
 * Закрытие вкладки браузера этот гард НЕ видит — для него компоненты сами вешают
 * `@HostListener('window:beforeunload')` (см. AdminAiModelComponent).
 */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (component) => {
  if (!component.hasUnsavedChanges()) return true;

  return inject(ConfirmService).confirm({
    title: 'Есть несохранённые изменения',
    message: 'Если уйти сейчас, внесённые правки пропадут.',
    confirmText: 'Уйти',
    cancelText: 'Остаться',
    danger: true,
  });
};
