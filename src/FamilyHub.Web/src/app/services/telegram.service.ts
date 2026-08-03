import { Injectable, inject, isDevMode } from '@angular/core';
import { DevLoggerService } from './dev-logger.service';

interface TelegramWebApp {
  initData: string;
  colorScheme: 'light' | 'dark';
  ready: () => void;
  expand: () => void;
  openLink: (url: string) => void;
  openTelegramLink: (url: string) => void;
  close: () => void;
  /** Системное подтверждение перед закрытием Mini App (аппаратный «назад» на Android его тоже
   * триггерит) — Angular Router popstate-guard'ов внутри Mini App не видит: сворачивание/закрытие
   * перехватывает сам Telegram, а не браузерная история. */
  enableClosingConfirmation: () => void;
  disableClosingConfirmation: () => void;
}

declare global {
  interface Window {
    Telegram?: { WebApp?: TelegramWebApp };
  }
}

const DEV_TG_ID_KEY = 'familyhub:devTgId';

@Injectable({ providedIn: 'root' })
export class TelegramService {
  private readonly log = inject(DevLoggerService);

  private get webApp(): TelegramWebApp | undefined {
    return window.Telegram?.WebApp;
  }

  init(): void {
    const inside = this.isInsideTelegram();
    this.log.log('tg', 'info', `init — inside=${inside}, colorScheme=${this.webApp?.colorScheme ?? 'n/a'}`);
    this.webApp?.ready();
    this.webApp?.expand();
  }

  getInitData(): string {
    return this.webApp?.initData ?? '';
  }

  isInsideTelegram(): boolean {
    return this.getInitData().length > 0;
  }

  getDevTelegramId(): string | null {
    // Сервер (DevAuthenticationHandler) и так отклоняет этот путь вне Development — практической
    // угрозы не было, но чтение/запись ?devTgId= молча происходило и в prod-сборке. isDevMode() —
    // тот же compile-time гейт, что и у DevPanelComponent, вырезается сборкой production (см.
    // аудит module-review-2026-08-02/08-web-frontend-angular.md, находка 4).
    if (!isDevMode()) return null;

    const fromQuery = new URLSearchParams(window.location.search).get('devTgId');
    if (fromQuery) {
      localStorage.setItem(DEV_TG_ID_KEY, fromQuery);
      return fromQuery;
    }
    return localStorage.getItem(DEV_TG_ID_KEY);
  }

  openExternalLink(url: string): void {
    this.log.log('tg', 'info', `openLink: ${url}`);
    if (this.webApp) {
      this.webApp.openLink(url);
    } else {
      window.open(url, '_blank', 'noopener,noreferrer');
    }
  }

  /** Открывает t.me-ссылку (например, share/url) нативным механизмом Telegram, не покидая приложение. */
  openTelegramLink(url: string): void {
    this.log.log('tg', 'info', `openTelegramLink: ${url}`);
    if (this.webApp) {
      this.webApp.openTelegramLink(url);
    } else {
      window.open(url, '_blank', 'noopener,noreferrer');
    }
  }

  /** См. doc-комментарий enableClosingConfirmation в TelegramWebApp — вызывать на шагах,
   * где случайное закрытие/сворачивание Mini App теряет введённые данные (ввод email-кода). */
  enableClosingConfirmation(): void {
    this.webApp?.enableClosingConfirmation();
  }

  disableClosingConfirmation(): void {
    this.webApp?.disableClosingConfirmation();
  }
}
