import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { AuthService, Me } from './auth.service';
import { authInterceptor } from './auth.interceptor';
import { TelegramService } from './telegram.service';

class RouterStub {
  url = '/home';
  navigate(): Promise<boolean> { return Promise.resolve(true); }
}

class TelegramStub {
  initData = '';
  devId: string | null = null;

  getInitData(): string { return this.initData; }
  getDevTelegramId(): string | null { return this.devId; }
  isInsideTelegram(): boolean { return this.initData.length > 0; }
}

const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));
const unauthorized = { status: 401, statusText: 'Unauthorized' };

const ME: Me = {
  userId: 'u1',
  lastName: 'Иванов',
  firstName: 'Иван',
  middleName: null,
  birthDate: '1990-01-01',
  gender: 0,
  profileComplete: true,
  provider: 'email',
  email: 'ivan@example.com',
  username: 'ivan',
  tgUsername: null,
  hasTelegram: false,
  hasPassword: true,
  timeZoneId: 'Europe/Moscow',
};

describe('AuthService: refresh и loadMe', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  let tg: TelegramStub;

  beforeEach(() => {
    // Маркер «cookie уже обновлена» общий для вкладок и живёт в localStorage — между тестами
    // его нужно убирать, иначе refresh из предыдущего теста «пропускает» POST в следующем.
    localStorage.removeItem('familyhub.authRefreshMarker');
    tg = new TelegramStub();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: Router, useValue: new RouterStub() },
        { provide: TelegramService, useValue: tg },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
  });

  afterEach(() => http.verify());

  it('loadMe: 401 → refresh → повтор /me возвращает пользователя', async () => {
    const pending = auth.loadMe();
    http.expectOne('/api/auth/me').flush({}, unauthorized);
    await tick();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await tick();
    http.expectOne('/api/auth/me').flush(ME);

    await expect(pending).resolves.toEqual(ME);
    expect(auth.me()).toEqual(ME);
    expect(auth.meLoadError()).toBeNull();
  });

  it('loadMe: refresh ответил 401 → unauthorized, me обнулён', async () => {
    auth.me.set(ME);

    const pending = auth.loadMe();
    http.expectOne('/api/auth/me').flush({}, unauthorized);
    await tick();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({}, unauthorized);

    await expect(pending).resolves.toBeNull();
    expect(auth.me()).toBeNull();
    expect(auth.meLoadError()).toBe('unauthorized');
  });

  it('loadMe: временная ошибка refresh → transient, me НЕ обнуляется', async () => {
    auth.me.set(ME);

    const pending = auth.loadMe();
    http.expectOne('/api/auth/me').flush({}, unauthorized);
    await tick();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' })
      .flush({}, { status: 429, statusText: 'Too Many Requests' });

    await expect(pending).resolves.toEqual(ME);
    expect(auth.me()).toEqual(ME);
    expect(auth.meLoadError()).toBe('transient');
  });

  it('loadMe в Telegram/dev-режиме не дёргает refresh', async () => {
    tg.devId = '42';

    const pending = auth.loadMe();
    http.expectOne('/api/auth/me').flush({}, unauthorized);

    await expect(pending).resolves.toBeNull();
    http.expectNone({ method: 'POST', url: '/api/auth/refresh' });
    expect(auth.meLoadError()).toBe('unauthorized');
  });

  it('refresh дедуплицирует параллельные вызовы', async () => {
    const first = auth.refresh();
    const second = auth.refresh();

    const refreshes = http.match({ method: 'POST', url: '/api/auth/refresh' });
    expect(refreshes).toHaveLength(1);
    refreshes[0].flush({});

    await expect(first).resolves.toBe('ok');
    await expect(second).resolves.toBe('ok');
  });

  it('не ротирует токен повторно, если cookie уже обновила другая вкладка', async () => {
    // Другая вкладка успела завершить refresh, пока мы ждали блокировку: она записала свой
    // маркер, cookie свежая. Второй POST выглядел бы для бэкенда как reuse-detection и отозвал
    // бы все сессии.
    const originalLocks = Object.getOwnPropertyDescriptor(navigator, 'locks');
    const request = vi.fn(<T>(_name: string, cb: () => Promise<T>) => {
      localStorage.setItem('familyhub.authRefreshMarker', 'другая-вкладка');
      return cb();
    });
    Object.defineProperty(navigator, 'locks', { configurable: true, value: { request } });

    try {
      await expect(auth.refresh()).resolves.toBe('ok');
      http.expectNone({ method: 'POST', url: '/api/auth/refresh' });
      expect(auth.refreshInFlight()).toBe(false);
      expect(localStorage.getItem('familyhub.authRefreshMarker')).toBe('другая-вкладка');
    } finally {
      if (originalLocks) Object.defineProperty(navigator, 'locks', originalLocks);
      else delete (navigator as unknown as { locks?: unknown }).locks;
    }
  });

  it('последовательные refresh оба уходят в API — маркер сравнивается по значению, не по времени', async () => {
    // Регрессия: пока метка писалась временем, второй refresh в ту же миллисекунду считал,
    // что cookie «уже обновлена», и молча пропускал POST — сессия оставалась с протухшим
    // access-токеном, а следующая 401 уводила на /login.
    const first = auth.refresh();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await expect(first).resolves.toBe('ok');

    const second = auth.refresh();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await expect(second).resolves.toBe('ok');
  });

  it('refresh сериализуется через Web Locks и обновляет общий маркер', async () => {
    const request = vi.fn(<T>(_name: string, cb: () => Promise<T>) => cb());
    const originalLocks = Object.getOwnPropertyDescriptor(navigator, 'locks');
    Object.defineProperty(navigator, 'locks', { configurable: true, value: { request } });

    try {
      const pending = auth.refresh();
      expect(auth.refreshInFlight()).toBe(true);

      http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
      await expect(pending).resolves.toBe('ok');

      expect(request).toHaveBeenCalledTimes(1);
      expect(request.mock.calls[0][0]).toBe('familyhub:auth-refresh');
      expect(localStorage.getItem('familyhub.authRefreshMarker')).toBeTruthy();
      expect(auth.refreshInFlight()).toBe(false);
    } finally {
      if (originalLocks) Object.defineProperty(navigator, 'locks', originalLocks);
      else delete (navigator as unknown as { locks?: unknown }).locks;
    }
  });
});
