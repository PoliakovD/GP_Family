import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { TelegramService } from './telegram.service';

/**
 * Telegram/dev — заголовки; PWA — cookie (withCredentials). Ошибки доступа маппятся
 * в навигацию: 401 в PWA-режиме → /login, 403 consent_required → /consent (задачи 2.3/2.4).
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tg = inject(TelegramService);
  const router = inject(Router);
  const initData = tg.getInitData();

  let headers = req.headers;
  if (initData) {
    headers = headers.set('Authorization', `tma ${initData}`);
  } else {
    const devId = tg.getDevTelegramId();
    if (devId) {
      headers = headers.set('X-Dev-TelegramId', devId);
    }
  }

  // Дев/стейджинг часто пробрасывается через ngrok: бесплатный тариф может показать
  // interstitial-страницу предупреждения для первых запросов сессии — этот заголовок
  // официально отключает её; на проде/без ngrok бэкенд его просто игнорирует.
  headers = headers.set('ngrok-skip-browser-warning', 'true');

  // Явный запрет любого кэширования GET-запросов к API. Обнаружен случай, воспроизводимый
  // только внутри реального Telegram-клиента (WebView) и не воспроизводимый прямыми HTTP-
  // запросами к тому же бэкенду/прокси: /api/auth/me иногда возвращал закэшированный где-то
  // на клиенте index.html вместо JSON от сервера. Раз бэкенд и dev-прокси при прямой проверке
  // всегда отвечают корректно — источник вероятно в кэш-слое самого WebView. Явные
  // no-store/no-cache — стандартная защита от именно такого класса проблем.
  headers = headers.set('Cache-Control', 'no-cache, no-store, must-revalidate').set('Pragma', 'no-cache');

  // withCredentials: same-origin cookie и так уходит, но это переживёт split-origin dev-прокси.
  return next(req.clone({ headers, withCredentials: true })).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        const isAuthEndpoint = req.url.startsWith('/api/auth');
        const isPwaMode = !initData && !tg.getDevTelegramId();

        if (error.status === 401 && isPwaMode && !isAuthEndpoint) {
          void router.navigate(['/login']);
        } else if (error.status === 403 && error.error?.code === 'consent_required') {
          void router.navigate(['/consent']);
        }
      }
      return throwError(() => error);
    }),
  );
};
