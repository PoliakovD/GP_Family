import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AiStatusService } from './ai-status.service';
import { ApiService } from './api.service';

/**
 * Глобальный опрос доступности ИИ: живёт вечно, пока приложение открыто, поэтому его обязательно
 * нужно гасить при выходе из аккаунта — иначе он продолжает ходить в API уже с /login.
 */
describe('AiStatusService', () => {
  const POLL_MS = 20_000;

  let getAiStatus: ReturnType<typeof vi.fn>;
  let ai: AiStatusService;

  beforeEach(() => {
    vi.useFakeTimers();
    getAiStatus = vi.fn(async () => ({ available: true }));
    TestBed.configureTestingModule({
      providers: [AiStatusService, { provide: ApiService, useValue: { getAiStatus } }],
    });
    ai = TestBed.inject(AiStatusService);
  });

  afterEach(() => vi.useRealTimers());

  it('опрашивает статус сразу и далее раз в 20 секунд', async () => {
    ai.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(getAiStatus).toHaveBeenCalledTimes(1);
    expect(ai.available()).toBe(true);

    await vi.advanceTimersByTimeAsync(POLL_MS * 2);
    expect(getAiStatus).toHaveBeenCalledTimes(3);
  });

  it('stop() прекращает опрос и забывает статус', async () => {
    ai.start();
    await vi.advanceTimersByTimeAsync(0);

    ai.stop();
    expect(ai.available()).toBeNull();

    await vi.advanceTimersByTimeAsync(POLL_MS * 5);
    expect(getAiStatus).toHaveBeenCalledTimes(1);
  });

  it('start() после stop() снова включает опрос (повторный вход)', async () => {
    ai.start();
    await vi.advanceTimersByTimeAsync(0);
    ai.stop();

    ai.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(getAiStatus).toHaveBeenCalledTimes(2);
  });

  it('повторный start() не создаёт второй таймер', async () => {
    ai.start();
    ai.start();
    await vi.advanceTimersByTimeAsync(POLL_MS);
    expect(getAiStatus).toHaveBeenCalledTimes(2);
  });
});
