import { ApplicationConfig, isDevMode, provideZoneChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
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
    provideHttpClient(withInterceptors([authInterceptor])),
    provideRouter(routes, withComponentInputBinding()),
    provideServiceWorker('ngsw-worker.js', {
      enabled: !isDevMode() && !isInsideTelegram(),
      registrationStrategy: 'registerWhenStable:30000',
    }),
  ],
};
