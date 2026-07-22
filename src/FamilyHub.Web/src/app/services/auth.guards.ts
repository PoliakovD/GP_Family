import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * PWA-режим без cookie-сессии → /login; Telegram/dev-режим аутентифицируется
 * заголовками на каждом запросе — гард пропускает.
 */
export const authGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.mode === 'telegram') return true;
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
