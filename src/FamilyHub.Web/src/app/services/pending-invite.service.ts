import { Injectable } from '@angular/core';

const STORAGE_KEY = 'familyhub.pendingInviteCode';

/**
 * Код инвайта, с которым гость попал на /join/:code до входа/регистрации (JoinInviteComponent) —
 * переживает переход на /login и обратно (sessionStorage, не сигнал: новая вкладка/перезагрузка
 * страницы во время формы входа не должны его терять). Погашается автоматически сразу после
 * успешной аутентификации — см. AppComponent (реагирует на тот же переход auth.me()/telegramBound()
 * в true, которым уже управляется первичная загрузка семей).
 */
@Injectable({ providedIn: 'root' })
export class PendingInviteService {
  set(code: string): void {
    try {
      sessionStorage.setItem(STORAGE_KEY, code);
    } catch {
      // Приватный режим/запрещённое хранилище — просто теряем удобство автопогашения,
      // пользователь всё ещё может погасить код вручную на экране семьи.
    }
  }

  /** Читает и сразу стирает — код погашается не более одного раза. */
  consume(): string | null {
    try {
      const code = sessionStorage.getItem(STORAGE_KEY);
      if (code) sessionStorage.removeItem(STORAGE_KEY);
      return code;
    } catch {
      return null;
    }
  }
}
