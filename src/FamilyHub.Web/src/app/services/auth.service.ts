import { Injectable, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TelegramService } from './telegram.service';
import { DevLoggerService } from './dev-logger.service';

export interface Me {
  userId: string;
  displayName: string;
  provider: 'telegram' | 'email' | 'dev';
  email: string | null;
  /** Видимый уникальный username (задаётся при PWA-регистрации либо копируется из Telegram при первом входе). */
  username: string | null;
  /** Зеркало Telegram @username — не уникален, обновляется на каждый TG-вход. */
  tgUsername: string | null;
  hasTelegram: boolean;
  hasPin: boolean;
}

export interface LinkTelegramStart {
  code: string;
  deepLink: string;
  expiresAt: string;
}

export interface ConsentStatus {
  accepted: boolean;
  version: string;
}

export interface ConsentText {
  version: string;
  text: string;
}

/**
 * Аутентификация в двух окружениях (этап 2 п.2.4): Telegram Mini App (initData через
 * интерцептор) и PWA (email+PIN, cookie-сессия). Плюс статус согласия ПДн (задача 2.3).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly tg = inject(TelegramService);
  private readonly log = inject(DevLoggerService);

  readonly me = signal<Me | null>(null);
  readonly consent = signal<ConsentStatus | null>(null);

  /** Общий in-flight запрос — см. loadMe(). */
  private pendingLoadMe: Promise<Me | null> | null = null;

  /** telegram/dev — вход неявный (заголовки интерцептора); pwa — нужна cookie-сессия. */
  get mode(): 'telegram' | 'pwa' {
    return this.tg.isInsideTelegram() || this.tg.getDevTelegramId() ? 'telegram' : 'pwa';
  }

  /**
   * null — не аутентифицирован (в PWA-режиме это сигнал показать /login).
   * authGuard вызывает это на КАЖДОЙ навигации, пока auth.me() пуст, а AppComponent — ещё
   * и при старте приложения; без дедупликации несколько таких вызовов, случившихся почти
   * одновременно (быстрое переключение вкладок сразу после открытия), уходили отдельными
   * HTTP-запросами и упирались в rate-limit "auth" эндпоинта (429). Пока предыдущий запрос
   * не завершился, повторные вызовы просто ждут тот же промис вместо нового запроса.
   */
  loadMe(): Promise<Me | null> {
    if (this.pendingLoadMe) {
      this.log.log('auth', 'info', 'loadMe() — переиспользую уже летящий запрос');
      return this.pendingLoadMe;
    }

    this.log.log('auth', 'info', 'GET /api/auth/me');
    this.pendingLoadMe = (async () => {
      try {
        const me = await firstValueFrom(this.http.get<Me>('/api/auth/me'));
        this.me.set(me);
        this.log.log('auth', 'info', `GET /api/auth/me ✓ provider=${me.provider}`);
        return me;
      } catch (e) {
        // 401 — по-настоящему не аутентифицирован, обнуляем. Любая другая ошибка (429 от
        // rate-limit'а, сетевой сбой, 5xx) — транзиентная и НЕ должна затирать уже известное
        // успешное состояние: иначе разовый сбой повторного вызова (например, из ngOnInit
        // компонента настроек) выглядел бы как разлогин и намертво вешал экран на "Загрузка…",
        // хотя пользователь всё ещё полноценно аутентифицирован (особенно заметно в
        // Telegram-режиме, где сессия per-request и в принципе не может "потеряться").
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        if (status === 401) {
          this.me.set(null);
        }
        this.log.log('auth', 'error', `GET /api/auth/me ✗ ${status} ${this.describeError(e)}`);
        return this.me();
      } finally {
        this.pendingLoadMe = null;
      }
    })();

    return this.pendingLoadMe;
  }

  registerStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/register/start', { email }));
  }

  async registerConfirm(
    email: string, code: string, pin: string, username: string, displayName: string | null,
  ): Promise<void> {
    await firstValueFrom(
      this.http.post<void>('/api/auth/register/confirm', { email, code, pin, username, displayName }),
    );
    await this.loadMe();
  }

  /** Проверка занятости username на форме регистрации (blur-хук, см. UsernameRules на бэкенде). */
  async checkUsernameAvailable(username: string): Promise<boolean> {
    const response = await firstValueFrom(
      this.http.get<{ available: boolean }>('/api/auth/username-available', { params: { username } }),
    );
    return response.available;
  }

  async login(email: string, pin: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/login', { email, pin }));
    await this.loadMe();
  }

  /** Забыли PIN: код на email (анти-enumeration — тот же ответ вне зависимости от наличия аккаунта). */
  resetPinStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/reset-pin/start', { email }));
  }

  async resetPinConfirm(email: string, code: string, newPin: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/reset-pin/confirm', { email, code, newPin }));
    await this.loadMe();
  }

  async logout(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/logout', {}));
    this.me.set(null);
  }

  linkEmailStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/link-email/start', { email }));
  }

  async linkEmailConfirm(email: string, code: string, pin: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/link-email/confirm', { email, code, pin }));
    await this.loadMe();
  }

  /** Привязка Telegram к текущему (email/PWA) аккаунту — код + deep-link, подтверждение в боте. */
  linkTelegramStart(): Promise<LinkTelegramStart> {
    return firstValueFrom(this.http.post<LinkTelegramStart>('/api/auth/link-telegram/start', {}));
  }

  async loadConsentStatus(): Promise<ConsentStatus> {
    const status = await firstValueFrom(this.http.get<ConsentStatus>('/api/consents/status'));
    this.consent.set(status);
    return status;
  }

  getConsentText(): Promise<ConsentText> {
    return firstValueFrom(this.http.get<ConsentText>('/api/consents/current'));
  }

  async acceptConsent(version: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/consents/accept', { version }));
    this.consent.set({ accepted: true, version });
  }

  async deleteAccount(): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/account/delete', { confirm: 'DELETE' }));
    this.me.set(null);
  }

  /**
   * Экспорт данных субъекта (zip). НЕ ссылка/window.open — обычная навигация браузера не
   * идёт через authInterceptor и не получает Authorization-заголовок Telegram-режима (в PWA
   * это работало только случайно, за счёт того, что cookie браузер прикладывает сам). Через
   * HttpClient запрос идёт по общему пайплайну и аутентифицируется одинаково в обоих режимах.
   */
  async exportAccountData(): Promise<Blob> {
    return firstValueFrom(this.http.get('/api/account/export', { responseType: 'blob' }));
  }

  /**
   * Диагностика для DevLogger: "200, но ошибка" почти всегда значит, что тело ответа не
   * распарсилось как JSON (см. loadMe()) — здесь важно увидеть Content-Type и хотя бы кусок
   * тела (HTML interstitial выглядит иначе, чем пустой ответ или обрыв соединения).
   */
  private describeError(e: unknown): string {
    if (!(e instanceof HttpErrorResponse)) return String(e);

    const contentType = e.headers?.get('content-type') ?? 'n/a';
    let bodySnippet: string;
    if (typeof e.error === 'string') {
      bodySnippet = e.error.slice(0, 150);
    } else if (e.error instanceof ProgressEvent) {
      bodySnippet = `ProgressEvent(type=${e.error.type})`; // сетевой обрыв/CORS — тело недоступно
    } else {
      try {
        bodySnippet = JSON.stringify(e.error)?.slice(0, 150) ?? String(e.error);
      } catch {
        bodySnippet = String(e.error);
      }
    }
    return `contentType=${contentType} url=${e.url} body="${bodySnippet}"`;
  }
}
