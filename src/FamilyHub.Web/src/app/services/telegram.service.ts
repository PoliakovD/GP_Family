import { Injectable, inject } from '@angular/core';
import { DevLoggerService } from './dev-logger.service';

interface TelegramWebApp {
  initData: string;
  colorScheme: 'light' | 'dark';
  ready: () => void;
  expand: () => void;
  openLink: (url: string) => void;
  close: () => void;
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
}
