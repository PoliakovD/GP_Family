import { WritableSignal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ToastService } from '../../shared/toast/toast.service';

/** Зеркалит FamilyHub.Domain.ValueObjects.PasswordRules на бэкенде: 8-100 симв., строчная +
 * заглавная латинские буквы + цифра. Единый источник истины — сервер; здесь только UX.
 * Общий с LoginComponent (форма регистрации/сброса пароля) — держим паттерн в одном месте. */
export const PASSWORD_PATTERN = /^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,100}$/;

/**
 * Обёртка "busy + toast на ошибку" — общая для всех вкладок настроек (перенесена из исходного
 * SettingsComponent.run()). Каждая вкладка держит свой собственный сигнал busy — действия из
 * разных вкладок не должны блокировать друг друга.
 */
export async function runBusy(
  busy: WritableSignal<boolean>,
  toast: ToastService,
  action: () => Promise<void>,
): Promise<void> {
  busy.set(true);
  try {
    await action();
  } catch (e) {
    toast.error(describeSettingsError(e));
  } finally {
    busy.set(false);
  }
}

/**
 * Код ошибки бэкенда → русское сообщение — единый стиль с LoginComponent.describe(), а не
 * третий вариант того же паттерна код→сообщение в каждой вкладке настроек.
 */
export function describeSettingsError(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    switch (e.error?.code) {
      case 'invalid_code': return 'Неверный или истёкший код подтверждения.';
      case 'email_taken': return 'Этот email уже привязан к другому аккаунту.';
      case 'weak_password': return 'Пароль — минимум 8 символов, обязательно строчная и заглавная латинские буквы и цифра.';
      case 'invalid_credentials': return 'Текущий пароль неверен.';
      case 'no_password': return 'У аккаунта ещё нет пароля — сначала привяжите email на вкладке «Профиль».';
    }
    if (e.status === 429) return 'Слишком много запросов — подождите немного.';
  }
  return 'Не получилось — попробуйте ещё раз.';
}
