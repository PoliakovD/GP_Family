import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { TelegramService } from './telegram.service';

/** Роутер-заглушка: фиксируем переходы, чтобы проверять «разлогинили / не разлогинили». */
class RouterStub {
  url = '/home';
  readonly navigates: { commands: unknown[]; queryParams?: Record<string, unknown> }[] = [];

  navigate(commands: unknown[], extras?: { queryParams?: Record<string, unknown> }): Promise<boolean> {
    this.navigates.push({ commands, queryParams: extras?.queryParams });
    return Promise.resolve(true);
  }
}

/** Telegram-заглушка: нужен только режим (initData присутствует / dev-заголовок). */
class TelegramStub {
  initData = '';
  devId: string | null = null;

  getInitData(): string { return this.initData; }
  getDevTelegramId(): string | null { return this.devId; }
  isInsideTelegram(): boolean { return this.initData.length > 0; }
}

/** Дать отработать микротаскам: refresh()/http.request() — промисы, ответ нужно успеть подложить. */
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

const unauthorized = { status: 401, statusText: 'Unauthorized' };

describe('authInterceptor: тихое обновление сессии', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let router: RouterStub;
  let tg: TelegramStub;

  beforeEach(() => {
    router = new RouterStub();
    tg = new TelegramStub();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: Router, useValue: router },
        { provide: TelegramService, useValue: tg },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
  });

  afterEach(() => http.verify());

  it('401 → refresh → повтор исходного запроса проходит прозрачно', async () => {
    const response = firstValueFrom(client.get('/api/families'));
    http.expectOne('/api/families').flush({}, unauthorized);
    await tick();

    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await tick();
    http.expectOne('/api/families').flush({ items: [] });

    await expect(response).resolves.toEqual({ items: [] });
    expect(router.navigates).toHaveLength(0);
  });

  it('не подменяет ошибку повтора исходной 401 (409 остаётся 409, без /login)', async () => {
    const error = firstValueFrom(client.post('/api/families', { name: 'x' })).catch((e) => e);
    http.expectOne({ method: 'POST', url: '/api/families' }).flush({}, unauthorized);
    await tick();

    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await tick();
    http.expectOne({ method: 'POST', url: '/api/families' })
      .flush({ code: 'conflict' }, { status: 409, statusText: 'Conflict' });

    await expect(error).resolves.toMatchObject({ status: 409 });
    expect(router.navigates).toHaveLength(0);
  });

  it('refresh ответил 401 → единственный переход на /login с expired и returnUrl', async () => {
    router.url = '/health/medications';
    const error = firstValueFrom(client.get('/api/medications')).catch((e) => e);
    http.expectOne('/api/medications').flush({}, unauthorized);
    await tick();

    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({}, unauthorized);

    await expect(error).resolves.toMatchObject({ status: 401 });
    expect(router.navigates).toEqual([
      { commands: ['/login'], queryParams: { expired: 1, returnUrl: '/health/medications' } },
    ]);
  });

  it('транзиентная ошибка refresh (429) не разлогинивает и не отдаёт 401', async () => {
    const error = firstValueFrom(client.get('/api/families')).catch((e) => e);
    http.expectOne('/api/families').flush({}, unauthorized);
    await tick();

    http.expectOne({ method: 'POST', url: '/api/auth/refresh' })
      .flush({}, { status: 429, statusText: 'Too Many Requests' });

    const err = await error;
    expect(err).toMatchObject({ status: 503 });
    expect(err.error.message).toBe('Не удалось продлить сессию — проверьте связь и попробуйте ещё раз.');
    expect(router.navigates).toHaveLength(0);
  });

  it('сетевая ошибка refresh не разлогинивает', async () => {
    const error = firstValueFrom(client.get('/api/families')).catch((e) => e);
    http.expectOne('/api/families').flush({}, unauthorized);
    await tick();

    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).error(new ProgressEvent('error'));

    await expect(error).resolves.toMatchObject({ status: 503 });
    expect(router.navigates).toHaveLength(0);
  });

  it('повторная 401 после refresh → ещё одна попытка, и только потом /login', async () => {
    const error = firstValueFrom(client.get('/api/families')).catch((e) => e);

    http.expectOne('/api/families').flush({}, unauthorized);
    await tick();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await tick();

    http.expectOne('/api/families').flush({}, unauthorized);
    await tick();
    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await tick();

    http.expectOne('/api/families').flush({}, unauthorized);

    await expect(error).resolves.toMatchObject({ status: 401 });
    expect(router.navigates).toHaveLength(1);
    http.expectNone({ method: 'POST', url: '/api/auth/refresh' });
  });

  it('параллельные 401 используют один refresh', async () => {
    const a = firstValueFrom(client.get('/api/a')).catch((e) => e);
    const b = firstValueFrom(client.get('/api/b')).catch((e) => e);

    http.expectOne('/api/a').flush({}, unauthorized);
    http.expectOne('/api/b').flush({}, unauthorized);
    await tick();

    const refreshes = http.match({ method: 'POST', url: '/api/auth/refresh' });
    expect(refreshes).toHaveLength(1);
    refreshes[0].flush({});
    await tick();

    http.expectOne('/api/a').flush({ a: 1 });
    http.expectOne('/api/b').flush({ b: 1 });

    await expect(a).resolves.toEqual({ a: 1 });
    await expect(b).resolves.toEqual({ b: 1 });
  });

  it('400 csrf_token_invalid на повторе после refresh перевыпускает токен и повторяет', async () => {
    const response = firstValueFrom(client.post('/api/families', { name: 'x' }));
    http.expectOne({ method: 'POST', url: '/api/families' }).flush({}, unauthorized);
    await tick();

    http.expectOne({ method: 'POST', url: '/api/auth/refresh' }).flush({});
    await tick();
    http.expectOne({ method: 'POST', url: '/api/families' })
      .flush({ code: 'csrf_token_invalid' }, { status: 400, statusText: 'Bad Request' });
    await tick();

    // Перевыпуск CSRF — GET /api/auth/me (он session-less, refresh не запускает).
    http.expectOne('/api/auth/me').flush({});
    await tick();
    http.expectOne({ method: 'POST', url: '/api/families' }).flush({ ok: true });

    await expect(response).resolves.toEqual({ ok: true });
    expect(router.navigates).toHaveLength(0);
  });

  it('/api/auth/me не рефрешит сам себя (этим занимается AuthService.loadMe)', async () => {
    const error = firstValueFrom(client.get('/api/auth/me')).catch((e) => e);
    http.expectOne('/api/auth/me').flush({}, unauthorized);

    await expect(error).resolves.toMatchObject({ status: 401 });
    http.expectNone({ method: 'POST', url: '/api/auth/refresh' });
    expect(router.navigates).toHaveLength(0);
  });

  it('Telegram-режим не пытается рефрешить PWA-сессию', async () => {
    tg.initData = 'query_id=1&hash=abc';
    const error = firstValueFrom(client.get('/api/families')).catch((e) => e);
    http.expectOne('/api/families').flush({}, unauthorized);

    await expect(error).resolves.toMatchObject({ status: 401 });
    http.expectNone({ method: 'POST', url: '/api/auth/refresh' });
    expect(router.navigates).toEqual([{ commands: ['/telegram-bind'], queryParams: undefined }]);
  });
});
