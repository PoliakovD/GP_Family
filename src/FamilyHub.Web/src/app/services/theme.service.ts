import { Injectable, signal } from '@angular/core';

export type ThemePreference = 'system' | 'light' | 'dark';

const STORAGE_KEY = 'familyhub:theme';

/**
 * Тема оформления (DESIGN §2.2): «Системная» — без атрибута, решает prefers-color-scheme (styles.scss);
 * «Светлая» / «Тёмная» — data-theme на <html>, перекрывает системную. Выбор — на устройство (localStorage),
 * как и в ОС: на телефоне и ноутбуке он может отличаться.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly preference = signal<ThemePreference>(this.read());

  constructor() {
    this.apply(this.preference());
  }

  set(pref: ThemePreference): void {
    this.preference.set(pref);
    try {
      if (pref === 'system') localStorage.removeItem(STORAGE_KEY);
      else localStorage.setItem(STORAGE_KEY, pref);
    } catch {
      // приватный режим / запрет хранилища — тема действует до перезагрузки
    }
    this.apply(pref);
  }

  private read(): ThemePreference {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      return v === 'light' || v === 'dark' ? v : 'system';
    } catch {
      return 'system';
    }
  }

  private apply(pref: ThemePreference): void {
    const root = document.documentElement;
    if (pref === 'system') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', pref);
  }
}
