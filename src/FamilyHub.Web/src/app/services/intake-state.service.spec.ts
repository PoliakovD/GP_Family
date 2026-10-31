import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { IntakeStateService } from './intake-state.service';
import { ApiService } from './api.service';
import { DevLoggerService } from './dev-logger.service';

/** Опрос бейджа «приём лекарств требует внимания» — раз в 5 минут, до выхода из аккаунта. */
describe('IntakeStateService — опрос бейджа', () => {
  const POLL_MS = 5 * 60_000;

  let getIntakeAttentionCount: ReturnType<typeof vi.fn>;
  let intake: IntakeStateService;

  beforeEach(() => {
    vi.useFakeTimers();
    getIntakeAttentionCount = vi.fn(async () => ({ count: 2 }));
    TestBed.configureTestingModule({
      providers: [
        IntakeStateService,
        { provide: ApiService, useValue: { getIntakeAttentionCount } },
        { provide: DevLoggerService, useValue: { log: vi.fn() } },
      ],
    });
    intake = TestBed.inject(IntakeStateService);
  });

  afterEach(() => vi.useRealTimers());

  it('startPolling обновляет бейдж раз в 5 минут', async () => {
    intake.startPolling();
    await vi.advanceTimersByTimeAsync(POLL_MS);
    expect(getIntakeAttentionCount).toHaveBeenCalledTimes(1);
    expect(intake.attention()).toBe(2);
  });

  it('stopPolling прекращает опрос — после выхода из аккаунта запросов больше нет', async () => {
    intake.startPolling();
    intake.stopPolling();

    await vi.advanceTimersByTimeAsync(POLL_MS * 3);
    expect(getIntakeAttentionCount).not.toHaveBeenCalled();
  });

  it('повторный startPolling не создаёт второй таймер', async () => {
    intake.startPolling();
    intake.startPolling();

    await vi.advanceTimersByTimeAsync(POLL_MS);
    expect(getIntakeAttentionCount).toHaveBeenCalledTimes(1);
  });
});
