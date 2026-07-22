import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TelegramService } from './telegram.service';

export interface Me {
  userId: string;
  displayName: string;
  provider: 'telegram' | 'email' | 'dev';
  email: string | null;
  hasTelegram: boolean;
  hasPin: boolean;
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

  readonly me = signal<Me | null>(null);
  readonly consent = signal<ConsentStatus | null>(null);

  /** telegram/dev — вход неявный (заголовки интерцептора); pwa — нужна cookie-сессия. */
  get mode(): 'telegram' | 'pwa' {
    return this.tg.isInsideTelegram() || this.tg.getDevTelegramId() ? 'telegram' : 'pwa';
  }

  /** null — не аутентифицирован (в PWA-режиме это сигнал показать /login). */
  async loadMe(): Promise<Me | null> {
    try {
      const me = await firstValueFrom(this.http.get<Me>('/api/auth/me'));
      this.me.set(me);
      return me;
    } catch {
      this.me.set(null);
      return null;
    }
  }

  registerStart(email: string): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/register/start', { email }));
  }

  async registerConfirm(email: string, code: string, pin: string, displayName: string | null): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/register/confirm', { email, code, pin, displayName }));
    await this.loadMe();
  }

  async login(email: string, pin: string): Promise<void> {
    await firstValueFrom(this.http.post<void>('/api/auth/login', { email, pin }));
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
}
