import { HttpClient, HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, firstValueFrom, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';
import { TelegramService } from './telegram.service';
import { safeReturnUrl } from './return-url';

/**
 * PWA-эндпоинты, где 401 — легитимный бизнес-ответ (неверный пароль, код и т.п.) или сам refresh,
 * а не признак истёкшей сессии: retry после refresh здесь бессмысленен (сессии ещё/уже нет),
 * плюс исключает бесконечный цикл на самом /refresh.
 *
 * /api/auth/me — отдельный случай: его дергают и на публичных страницах без гардов (/join/:code,
 * /privacy, /consent-text — см. AppComponent.initAuth() и JoinInviteComponent.resolveAuth()),
 * чтобы узнать, залогинен ли гость. 401 там — не разлогин, а штатный ответ для анонимного
 * визитёра: AuthService.loadMe() сам корректно обнуляет me() по 401 (см. loadMe), а редиректом на
 * /login занимается authGuard там, где страница ДЕЙСТВИТЕЛЬНО защищена. Без этого исключения
 * анонимный заход на /join/:code уводило на /login раньше, чем успевал отрендериться лендинг
 * приглашения — ветка ниже запускала refresh, он неизбежно проваливался (нет сессии вообще), и
 * catchError уводил на /login.
 */
const SESSION_LESS_AUTH_PATHS = [
  '/api/auth/register/start',
  '/api/auth/register/confirm',
  '/api/auth/username-available',
  '/api/auth/login',
  '/api/auth/reset-password/start',
  '/api/auth/reset-password/confirm',
  '/api/auth/refresh',
  '/api/auth/me',
];



/**
 * Запрос уже повторён после перевыпуска CSRF-токена — второй 400 csrf_token_invalid не ретраим
 * (иначе бесконечный цикл, если сервер отвергает токен по другой причине).
 */
const CSRF_RETRIED = new HttpContextToken<boolean>(() => false);

/**
 * Сколько раз этот запрос уже повторяли после тихого refresh. Нужен, чтобы повторная 401 после
 * успешного refresh не уходила в бесконечный цикл: бэкенд допускает вторую ротацию за время
 * жизни одного access-токена (см. SessionValidityChecker), поэтому одна повторная попытка —
 * штатная ситуация, а не разлогин. После лимита считаем сессию мёртвой.
 */
const REFRESH_ATTEMPTS = new HttpContextToken<number>(() => 0);
const MAX_REFRESH_ATTEMPTS = 2;

/** Общий in-flight перевыпуск CSRF-токена: GET /api/auth/me выставляет свежую пару cookie. */
let csrfReissueInFlight: Promise<void> | null = null;

function reissueCsrfToken(http: HttpClient): Promise<void> {
  if (!csrfReissueInFlight) {
    csrfReissueInFlight = firstValueFrom(http.get('/api/auth/me', { withCredentials: true }))
      .then(() => undefined)
      .finally(() => {
        csrfReissueInFlight = null;
      });
  }
  return csrfReissueInFlight;
}

/** Единственная точка ухода на /login по истёкшей сессии (несколько параллельных 401 — один переход). */
function redirectToLogin(router: Router): void {
  if (router.url.startsWith('/login')) return;
  const returnUrl = safeReturnUrl(router.url);
  void router.navigate(['/login'], {
    queryParams: returnUrl ? { expired: 1, returnUrl } : { expired: 1 },
  });
}

/**
 * Ошибка для случая «refresh не удался по временной причине» (сеть, 429, 5xx). Отдавать наверх
 * исходный 401 нельзя: `toApiError` превратит его в «Сессия истекла — войдите снова.», хотя
 * сессия жива. 503 + русский `message` даёт честный текст и НЕ разлогинивает — следующий 401
 * попробует refresh ещё раз.
 */
function refreshUnavailable(original: HttpErrorResponse): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 503,
    statusText: 'Session refresh failed',
    url: original.url ?? undefined,
    error: {
      code: 'session_refresh_failed',
      message: 'Не удалось продлить сессию — проверьте связь и попробуйте ещё раз.',
    },
  });
}

/**
 * Telegram/dev — заголовки; PWA — access-токен в httpOnly cookie (withCredentials).
 * 401 в PWA-режиме на защищённом эндпоинте → тихий `/api/auth/refresh` + повтор исходного
 * запроса (access-токен короткоживущий, это штатный сценарий, а не разлогин). На /login уводим
 * ТОЛЬКО когда refresh явно ответил 401; временная ошибка refresh (сеть/429/5xx) разлогином не
 * считается — наверх уходит 503 с понятным текстом, а следующий 401 попробует ещё раз.
 * 403 consent_required → /consent (задачи 2.3/2.4).
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
      // Админ-панель (ADR-0009) — полностью отдельная identity-система (cookie familyhub.admin,
      // AuthSchemes.Admin), не PWA-сессия. Без этого исключения 401 от /api/admin/* (например,
      // от AdminApiService.checkSession() при неавторизованном заходе) запускал бы PWA-refresh
      // и после его провала уводил на /login — затирая /admin/login, на который должен был
      // отправить adminGuard. AdminApiService/adminGuard сами решают, что делать с 401 отсюда.
      const isAdminPath = req.url.startsWith('/api/admin');

      // Сервер проверяет CSRF у каждого мутирующего PWA-запроса (аудит security-audit-2026-10, M4),
      // а CSRF-cookie выдаёт только GET /api/auth/me. Если её нет (новая сессия браузера до первого
      // /me, вытеснена) — перевыпускаем и повторяем запрос один раз. Повтор — через http.request,
      // а не next(): так он снова проходит xsrf-интерцептор Angular и получает свежий заголовок.
      if (error.status === 400 && error.error?.code === 'csrf_token_invalid' && isPwaMode
        && !req.context.get(CSRF_RETRIED)) {
        // Ошибка самого перевыпуска — отдаём исходную 400; ошибка повтора (409/422 и т.п.) — как есть.
        return from(reissueCsrfToken(http).then(() => true, () => false)).pipe(
          switchMap((reissued) => reissued
            ? http.request(req.clone({ context: req.context.set(CSRF_RETRIED, true) }))
            : throwError(() => error)),
        );
      }

      if (error.status === 401 && isPwaMode && !isSessionLessPath && !isAdminPath) {
        const attempts = req.context.get(REFRESH_ATTEMPTS);
        // Лимит исчерпан — сессия действительно мёртва (или бэкенд отвергает только что
        // выданный access-токен): уводим на /login один раз и отдаём исходную 401.
        if (attempts >= MAX_REFRESH_ATTEMPTS) {
          redirectToLogin(router);
          return throwError(() => error);
        }

        return from(auth.refresh()).pipe(
          switchMap((outcome) => {
            if (outcome === 'unauthorized') {
              // Только явный 401 от /refresh = refresh-сессии больше нет (истекла/отозвана).
              // /login?expired=1&returnUrl=… — с объяснением и возвратом на исходную страницу.
              redirectToLogin(router);
              return throwError(() => error);
            }
            if (outcome === 'transient') {
              // Сеть/429/5xx: сессия, скорее всего, жива. НЕ разлогиниваем и НЕ отдаём 401
              // (иначе UI напишет «Сессия истекла — войдите снова.») — отдаём честную
              // временную ошибку; следующий 401 попробует refresh ещё раз.
              return throwError(() => refreshUnavailable(error));
            }
            // Повтор — через http.request, а не next(): он снова проходит весь пайплайн (в т.ч.
            // CSRF-ветку выше, если пара токенов успела протухнуть) и ограничен REFRESH_ATTEMPTS.
            // Ошибку повтора НЕ подменяем исходной 401 — иначе 409/422/500 выглядели бы как
            // «сессия истекла» и разлогинивали только что обновлённую сессию.
            return http.request(req.clone({
              context: req.context.set(REFRESH_ATTEMPTS, attempts + 1),
            }));
          }),
        );
      }

      // Реальный Telegram Mini App: 401 здесь значит, что lookup-only хендлер больше не находит
      // TelegramId (например, только что отвязали Telegram в этой же сессии через revoke, или
      // TelegramId отвязан с другого устройства) — как и в PWA-ветке выше, ретраить нечего,
      // сессии в telegram-режиме нет вообще; ведём на повторную привязку.
      if (error.status === 401 && initData && error.error?.code === 'init_data_expired') {
        // initData старше Telegram:MaxInitDataAge (аудит security-audit-2026-10, M6) — аккаунт
        // в порядке, нужны только свежие данные от Telegram: просим перезапустить Mini App.
        if (!router.url.startsWith('/telegram-expired')) void router.navigate(['/telegram-expired']);
        return throwError(() => error);
      }

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
