import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService, Me } from './auth.service';
import { TelegramService } from './telegram.service';
import { ToastService } from '../shared/toast/toast.service';
import { safeReturnUrl } from './return-url';

/** Один короткий повтор при транзиентном сбое (429/сеть) — окно rate-limit'а обычно уже открыто
 * заново через секунду; лишний запрос дешевле, чем неверно принятое решение гарда. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function loadMeWithRetry(auth: AuthService): Promise<Me | null> {
  const me = await auth.loadMe();
  if (me !== null || auth.meLoadError() !== 'transient') return me;
  await delay(800);
  return auth.loadMe();
}

/**
 * PWA-режим без cookie-сессии → /login; Telegram/dev-режим аутентифицируется
 * заголовками на каждом запросе — гард пропускает, но для РЕАЛЬНОГО Telegram Mini App
 * (не dev-заголовка) сперва проверяет привязку TelegramId к аккаунту: без неё
 * TelegramMiniAppAuthenticationHandler теперь lookup-only и отклонит любой запрос (401),
 * пока пользователь не пройдёт email+OTP привязку (см. TelegramBindComponent).
 */
export const authGuard: CanActivateFn = async (_route, state) => {
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

  const me = await loadMeWithRetry(auth);
  if (me !== null) return true;
  // Транзиентный сбой (429/сеть/5xx) — не значит "не аутентифицирован": не выгоняем на /login,
  // страница откроется, а её собственные запросы либо пройдут (сессия жива), либо получат 401 и
  // authInterceptor обработает это сам (refresh, а при неудаче — уже он уведёт на /login).
  if (auth.meLoadError() === 'transient') return true;
  // returnUrl — после входа вернуть туда, куда шли (диплинк из пуша, ссылка из чата).
  const returnUrl = safeReturnUrl(state?.url);
  return router.createUrlTree(['/login'], returnUrl ? { queryParams: { returnUrl } } : undefined);
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

/**
 * ФИО/ДР/пол обязательны (identity rework), но PWA-регистрация и Telegram-привязка заполняют
 * их по-разному: PWA — сразу при регистрации (Me.profileComplete всегда true после неё),
 * Telegram — НЕТ (initData не источник профиля, см. TelegramBindingService) — этот гард ловит
 * именно такой недозаполненный аккаунт на любой последующей защищённой странице, не только
 * сразу после привязки (тот же приём, что и consentGuard рядом).
 */
export const profileGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const toast = inject(ToastService);

  const cached = auth.me();
  if (cached?.profileComplete) return true;

  const me = cached ?? (await loadMeWithRetry(auth));
  if (me?.profileComplete) return true;
  if (me === null && auth.meLoadError() === 'transient') {
    // Не смогли проверить профиль (429/сеть) — это НЕ то же самое, что «профиль не заполнен»:
    // authGuard уже подтвердил вход, значит скорее всего профиль давно заполнен, и отправлять
    // на /profile-setup из-за сетевого сбоя неверно. Пропускаем; если профиль правда не заполнен,
    // это вскроется на следующей навигации, когда /me снова ответит.
    toast.error('Не удалось проверить профиль — попробуйте обновить страницу');
    return true;
  }
  return router.createUrlTree(['/profile-setup']);
};
