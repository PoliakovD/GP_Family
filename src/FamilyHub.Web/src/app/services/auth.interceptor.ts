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
