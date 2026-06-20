// Тонкая обёртка над window.Telegram.WebApp (SDK подключён в index.html).
// Вне Telegram (обычный браузер, dev) WebApp отсутствует — все функции тут безопасно
// деградируют в no-op/заглушки, чтобы Mini App можно было открыть и в браузере для отладки.

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

export function getWebApp(): TelegramWebApp | undefined {
  return window.Telegram?.WebApp;
}

export function initTelegram(): void {
  const webApp = getWebApp();
  webApp?.ready();
  webApp?.expand();
}

export function getInitData(): string {
  return getWebApp()?.initData ?? '';
}

export function isInsideTelegram(): boolean {
  return getInitData().length > 0;
}

// Открытие presigned-URL вложений/PDF: в Telegram — через openLink (системный браузер),
// в обычном браузере (dev) — обычный window.open.
export function openExternalLink(url: string): void {
  const webApp = getWebApp();
  if (webApp) {
    webApp.openLink(url);
  } else {
    window.open(url, '_blank', 'noopener,noreferrer');
  }
}
