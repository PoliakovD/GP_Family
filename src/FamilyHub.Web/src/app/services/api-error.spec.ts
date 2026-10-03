import { describe, expect, it } from 'vitest';
import { HttpErrorResponse } from '@angular/common/http';
import { toApiError } from './api-error';

const http = (status: number, error: unknown, statusText = 'OK') =>
  new HttpErrorResponse({ status, error, statusText });

describe('toApiError', () => {
  it('never shows statusText: HTTP/2 500 without body reads "OK" otherwise', () => {
    const err = toApiError(http(500, null, 'OK'));
    expect(err.message).toBe('Сервер временно недоступен. Попробуйте чуть позже.');
  });

  it('status 0 (offline) → understandable Russian text instead of "Unknown Error"', () => {
    expect(toApiError(http(0, null, 'Unknown Error')).message).toContain('Нет связи с сервером');
  });

  it('keeps a Russian string body as is', () => {
    expect(toApiError(http(400, 'Имя семьи не может быть пустым.')).message).toBe('Имя семьи не может быть пустым.');
  });

  it('keeps a Russian {message} and exposes it as detail', () => {
    const err = toApiError(http(409, { message: 'Вы уже состоите в этой семье.', familyId: 'x' }));
    expect(err.message).toBe('Вы уже состоите в этой семье.');
    expect(err.detail).toBe('Вы уже состоите в этой семье.');
  });

  it('maps a bare {code} to Russian text and keeps the code', () => {
    const err = toApiError(http(409, { code: 'last_admin', families: [] }));
    expect(err.code).toBe('last_admin');
    expect(err.message).toContain('единственный администратор');
  });

  it('treats a code-like {reason} as a code (invite preview)', () => {
    const err = toApiError(http(409, { reason: 'expired' }));
    expect(err.code).toBe('expired');
    expect(err.message).toBe('Срок действия приглашения истёк.');
  });

  it('shows a human {reason} from the LLM gate', () => {
    const err = toApiError(http(422, { code: 'rejected', reason: 'Похоже на персональные данные.' }));
    expect(err.code).toBe('rejected');
    expect(err.message).toBe('Похоже на персональные данные.');
  });

  it('hides HTML proxy pages and English technical messages', () => {
    expect(toApiError(http(502, '<html><body>Bad gateway</body></html>')).message).toContain('Сервер временно недоступен');
    expect(toApiError(http(400, { message: 'ids must not be empty' })).message).toContain('Проверьте введённые данные');
  });

  it('unknown code falls back to status text', () => {
    expect(toApiError(http(404, { code: 'whatever' })).message).toBe('Не найдено — возможно, это уже удалили.');
  });

  it('non-HTTP error → generic Russian text', () => {
    expect(toApiError(new Error('boom')).message).toBe('Что-то пошло не так. Попробуйте ещё раз.');
  });
});
