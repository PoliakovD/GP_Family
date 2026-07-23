import { Injectable, signal } from '@angular/core';

export type CookieConsentChoice = 'accepted' | 'declined';

const STORAGE_KEY = 'familyhub:cookieConsent';

/**
 * Единственный cookie приложения — строго необходимая сессия PWA-входа (familyhub.auth,
 * HttpOnly). Трекинга/аналитики нет. Баннер — информационный (полный Принять/Отклонить по
 * решению пользователя), но отклонение не блокирует вход: без этого cookie сессия
 * невозможна технически, поэтому "Отклонить" просто скрывает баннер и показывает пояснение
 * на форме входа, а не отключает функциональность.
 */
@Injectable({ providedIn: 'root' })
export class CookieConsentService {
  readonly choice = signal<CookieConsentChoice | null>(this.readStored());

  private readStored(): CookieConsentChoice | null {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw === 'accepted' || raw === 'declined' ? raw : null;
  }

  setChoice(choice: CookieConsentChoice): void {
    localStorage.setItem(STORAGE_KEY, choice);
    this.choice.set(choice);
  }
}
