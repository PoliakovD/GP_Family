import { ApplicationConfig, isDevMode, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter, withComponentInputBinding, withRouterConfig } from '@angular/router';
import { provideServiceWorker } from '@angular/service-worker';
import { authInterceptor } from './services/auth.interceptor';
import { routes } from './app.routes';

/**
 * Этап 3 (гибридный доступ): SW нужен для PWA — офлайн-старт оболочки + установка на телефон
 * (задел под push/сообщения). Внутри Telegram Mini App НЕ регистрируем: там не нужен, а на
 * WebView уже был баг с кэшем index.html вместо JSON-ответа /api/auth/me (см. auth.interceptor.ts).
 * telegram-web-app.js грузится синхронно в <head> раньше main.ts (см. index.html), поэтому
 * window.Telegram?.WebApp?.initData уже доступен здесь — тот же признак, что и в
 * TelegramService.isInsideTelegram() (глобальный тип Window.Telegram объявлен там же).
 */
const isInsideTelegram = (): boolean => !!window.Telegram?.WebApp?.initData;

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    // withXsrfConfiguration: CSRF-защита PWA-сессии сверх SameSite=Lax (см. аудит
    // module-review-2026-08-02/01-auth-identity.md, находка 4). Angular сам читает cookie
    // XSRF-TOKEN (выставляется только вместе с PWA-сессией, см. PwaSessionCookieWriter) и
    // подставляет её значение в заголовок X-XSRF-TOKEN на каждый мутирующий запрос — имена
    // указаны явно, чтобы не зависеть от дефолтов Angular (сервер настроен на те же имена,
    // см. Program.cs AddAntiforgery).
    provideHttpClient(
      withInterceptors([authInterceptor]),
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
    ),
    // canceledNavigationResolution: 'computed' — по умолчанию ('replace') отменённая
    // popstate-навигация (см. pendingCodeGuard: пользователь нажал «Остаться» в диалоге)
    // восстанавливает URL, но НЕ позицию в истории браузера; после пары отмен нативная
    // кнопка «назад»/«вперёд» начинает прыгать через записи истории. 'computed' — штатное
    // решение Angular Router именно для этого случая.
    provideRouter(routes, withComponentInputBinding(), withRouterConfig({ canceledNavigationResolution: 'computed' })),
    provideServiceWorker('/ngsw-worker.js', {
      enabled: !isDevMode() && !isInsideTelegram(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
