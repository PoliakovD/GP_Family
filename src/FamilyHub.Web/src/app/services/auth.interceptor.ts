import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, firstValueFrom, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { TelegramService } from './telegram.service';

/**
 * PWA-эндпоинты, где 401 — легитимный бизнес-ответ (неверный пароль, код и т.п.) или сам refresh,
 * а не признак истёкшей сессии: retry после refresh здесь бессмысленен (сессии ещё/уже нет),
 * плюс исключает бесконечный цикл на самом /refresh.
 */
const SESSION_LESS_AUTH_PATHS = [
  '/api/auth/register/start',
  '/api/auth/register/confirm',
  '/api/auth/username-available',
  '/api/auth/login',
  '/api/auth/reset-password/start',
  '/api/auth/reset-password/confirm',
  '/api/auth/refresh',
];

/** Общий in-flight refresh — параллельные 401 не должны бить по /api/auth/refresh каждый отдельно. */
let refreshInFlight: Promise<boolean> | null = null;

function ensureRefreshed(http: HttpClient): Promise<boolean> {
  if (!refreshInFlight) {
    refreshInFlight = firstValueFrom(http.post('/api/auth/refresh', {}, { withCredentials: true }))
      .then(() => true)
      .catch(() => false)
      .finally(() => {
        refreshInFlight = null;
      });
  }
  return refreshInFlight;
}

/**
 * Telegram/dev — заголовки; PWA — access-токен в httpOnly cookie (withCredentials).
 * 401 в PWA-режиме на защищённом эндпоинте → однократный /api/auth/refresh + повтор исходного
 * запроса (access-токен короткоживущий, это штатный сценарий, а не разлогин); неудачный refresh
 * → /login. 403 consent_required → /consent (задачи 2.3/2.4).
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const tg = inject(TelegramService);
  const router = inject(Router);
  const http = inject(HttpClient);
  const auth = inject(AuthService);
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
      if (!(error instanceof HttpErrorResponse)) return throwError(() => error);

      const isPwaMode = !initData && !tg.getDevTelegramId();
      const isSessionLessPath = SESSION_LESS_AUTH_PATHS.some((p) => req.url.startsWith(p));

      if (error.status === 401 && isPwaMode && !isSessionLessPath) {
        return from(ensureRefreshed(http)).pipe(
          switchMap((refreshed) =>
            refreshed ? next(req.clone({ headers, withCredentials: true })) : throwError(() => error)),
          catchError(() => {
            void router.navigate(['/login']);
            return throwError(() => error);
          }),
        );
      }

      // Реальный Telegram Mini App: 401 здесь значит, что lookup-only хендлер больше не находит
      // TelegramId (например, только что отвязали Telegram в этой же сессии через revoke, или
      // TelegramId отвязан с другого устройства) — как и в PWA-ветке выше, ретраить нечего,
      // сессии в telegram-режиме нет вообще; ведём на повторную привязку.
      if (error.status === 401 && initData) {
        auth.telegramBound.set(false);
        void router.navigate(['/telegram-bind']);
        return throwError(() => error);
      }

      if (error.status === 403 && error.error?.code === 'consent_required') {
        void router.navigate(['/consent']);
      }
      return throwError(() => error);
    }),
  );
};
