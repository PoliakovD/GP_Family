import { inject } from '@angular/core';
import { CanDeactivateFn } from '@angular/router';
import { ConfirmService } from '../shared/confirm/confirm.service';

/** Компонент-визард (LoginComponent, TelegramBindComponent) с шагом, где уже выдан email-код. */
export interface HasPendingCodeEntry {
  hasPendingCodeEntry(): boolean;
}

/**
 * Защита шага ввода кода от случайной потери (нативная кнопка «назад», навигация роутером):
 * шаги визардов — сигналы внутри компонента, а не роуты, поэтому уход с роута разрушает
 * компонент и вместе с ним введённые пароль/username/код. Запросить код заново можно не больше
 * трёх раз в час (EmailOtpService.MaxActiveCodesPerHour) — случайный уход может запереть
 * человека на регистрации на час, поэтому не молчим, а переспрашиваем.
 *
 * Явные кнопки «Назад»/«Изменить данные» на самих шагах этим НЕ гейтятся — это внутренняя
 * навигация компонента (смена step()), не уход с роута, CanDeactivate её не видит.
 */
export const pendingCodeGuard: CanDeactivateFn<HasPendingCodeEntry> = (component) => {
  if (!component.hasPendingCodeEntry()) return true;

  return inject(ConfirmService).confirm({
    title: 'Прервать ввод кода?',
    message: 'Форму придётся заполнить заново. Новый код можно запросить не больше трёх раз в час.',
    confirmText: 'Прервать',
    cancelText: 'Остаться',
    danger: true,
  });
};
