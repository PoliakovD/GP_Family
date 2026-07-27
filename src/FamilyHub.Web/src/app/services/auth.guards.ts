import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';
import { TelegramService } from './telegram.service';

/**
 * PWA-режим без cookie-сессии → /login; Telegram/dev-режим аутентифицируется
 * заголовками на каждом запросе — гард пропускает, но для РЕАЛЬНОГО Telegram Mini App
 * (не dev-заголовка) сперва проверяет привязку TelegramId к аккаунту: без неё
 * TelegramMiniAppAuthenticationHandler теперь lookup-only и отклонит любой запрос (401),
 * пока пользователь не пройдёт email+OTP привязку (см. TelegramBindComponent).
 */
export const authGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const tg = inject(TelegramService);
  const router = inject(Router);

  if (auth.mode === 'telegram') {
    if (!tg.isInsideTelegram()) return true; // dev-заголовок — DevAuthenticationHandler авто-создаёт

    if (auth.telegramBound() === true) return true;
    const bound = await auth.ensureTelegramBound();
    return bound ? true : router.createUrlTree(['/telegram-bind']);
  }

  if (auth.me() !== null) return true;

  const me = await auth.loadMe();
  return me !== null ? true : router.createUrlTree(['/login']);
};

/** Данные обрабатываются только после принятия актуального согласия ПДн (задача 2.3). */
export const consentGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const cached = auth.consent();
  if (cached?.accepted) return true;

  try {
    const status = await auth.loadConsentStatus();
    return status.accepted ? true : router.createUrlTree(['/consent']);
  } catch {
    // Не аутентифицирован — authGuard уже направил куда надо; не блокируем повторно.
    return true;
  }
};
