import { APIRequestContext, Page, expect, request as pwRequest } from '@playwright/test';
import { API_URL } from './env';

/** Уникальный Telegram ID на сценарий — свой изолированный пользователь без пересечений между тестами. */
export function freshTgId(): number {
  return 1_000_000_000 + Math.floor(Math.random() * 8_000_000_000);
}

/** API-клиент, аутентифицированный dev-заголовком (то же, что делает фронт после ?devTgId=). */
export async function apiAs(tgId: number): Promise<APIRequestContext> {
  return pwRequest.newContext({ baseURL: API_URL, extraHTTPHeaders: { 'X-Dev-TelegramId': String(tgId) } });
}

async function ok<T = unknown>(res: { ok(): boolean; status(): number; text(): Promise<string>; json(): Promise<T> }, what: string): Promise<T> {
  if (!res.ok()) throw new Error(`${what}: HTTP ${res.status()} ${await res.text()}`);
  const body = await res.text();
  return (body ? JSON.parse(body) : undefined) as T;
}

/** Согласие ПДн + профиль — то, что новый пользователь проходит на экранах consent/profile-setup. */
export async function onboard(api: APIRequestContext, opts: { last: string; first: string; middle?: string }): Promise<void> {
  const current = await ok<{ version: string }>(await api.get('/api/consents/current'), 'consent current');
  await ok(await api.post('/api/consents/accept', { data: { version: current.version } }), 'consent accept');
  await ok(await api.put('/api/account/profile', {
    data: {
      lastName: opts.last, firstName: opts.first, middleName: opts.middle ?? null,
      birthDate: '1990-05-04', gender: 0,
    },
  }), 'profile');
}

/** Пользователь с согласием и профилем, вошедший по dev-заголовку. */
export async function newUser(last: string, first: string): Promise<{ tgId: number; api: APIRequestContext }> {
  const tgId = freshTgId();
  const api = await apiAs(tgId);
  await onboard(api, { last, first });
  return { tgId, api };
}

export async function createFamily(api: APIRequestContext, name: string): Promise<string> {
  const body = await ok<{ id: string }>(await api.post('/api/families', { data: { name } }), 'create family');
  return body.id;
}

export async function createInviteCode(api: APIRequestContext, familyId: string): Promise<string> {
  const invite = await ok<{ code: string }>(await api.post(`/api/families/${familyId}/invites`, {
    data: { targetUserId: null, assignedRole: 0, maxUses: 5, expiresAt: null },
  }), 'create invite');
  return invite.code;
}

export async function createAnalysis(api: APIRequestContext, title: string): Promise<string> {
  const rec = await ok<{ id: string }>(await api.post('/api/medical-records', {
    data: { recordDate: '2026-06-09', doctor: 'Иванов Иван Иванович', description: null, hideFromFamilyIds: null },
  }), 'create record');
  await ok(await api.put(`/api/medical-records/${rec.id}`, {
    data: { recordDate: '2026-06-09', doctor: 'Иванов Иван Иванович', description: null, title },
  }), 'set title');
  return rec.id;
}

export async function addIndicator(
  api: APIRequestContext, recordId: string,
  ind: { name: string; value: string; unit: string; low: string; high: string },
): Promise<void> {
  await ok(await api.post(`/api/medical-records/${recordId}/indicators`, {
    data: { displayName: ind.name, valueRaw: ind.value, unit: ind.unit, refLowText: ind.low, refHighText: ind.high, refText: null },
  }), 'add indicator');
}

export async function attachFile(api: APIRequestContext, recordId: string): Promise<void> {
  await ok(await api.post(`/api/medical-records/${recordId}/attachments`, {
    multipart: { file: { name: 'scan.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4 e2e scan') } },
  }), 'attach file');
}

/** Открывает приложение как пользователь tgId: ?devTgId= кладётся фронтом в localStorage (dev-сборка). */
export async function openAs(page: Page, tgId: number, path = '/'): Promise<void> {
  const url = new URL(path, API_URL);
  url.searchParams.set('devTgId', String(tgId));
  await page.goto(url.toString());
  // Шапка/меню приложения появились — вход состоялся (на consent/profile-setup её нет, это учтено в сценарии онбординга).
  await expect(page).not.toHaveURL(/\/login/);
}
