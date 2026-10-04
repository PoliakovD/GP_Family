import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ExtractionJobStatus, ExtractionStage } from '../../models/types';
import type { ExtractionStatusResponse } from '../../models/types';
import { ExtractionPoller } from './extraction-poller';

const status = (over: Partial<ExtractionStatusResponse> = {}): ExtractionStatusResponse => ({
  status: ExtractionJobStatus.Running, stage: ExtractionStage.Ocr, indicatorCount: 0, error: null,
  totalFiles: 1, processedFiles: 0, createdAt: '', completedAt: null, queuePosition: 0,
  waitingForAi: false, ...over,
});

const record = { id: 'r1' };
const opts = { intervalMs: 1000, waitingIntervalMs: 10_000, maxFailures: 3 };

/** Даём отработать промисам внутри тика (fetchStatus → onStatus → onFinished). */
const flush = async () => { for (let i = 0; i < 5; i++) await Promise.resolve(); };

describe('ExtractionPoller', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function setup(responses: Array<ExtractionStatusResponse | Error>) {
    const queue = [...responses];
    const hooks = {
      fetchStatus: vi.fn(async () => {
        const next = queue.length > 1 ? queue.shift()! : queue[0];
        if (next instanceof Error) throw next;
        return next;
      }),
      onStatus: vi.fn(),
      onFinished: vi.fn(),
      onGaveUp: vi.fn(),
    };
    return { hooks, poller: new ExtractionPoller(hooks, opts) };
  }

  it('polls immediately and on every interval until a terminal status', async () => {
    const { hooks, poller } = setup([status(), status(), status({ status: ExtractionJobStatus.Completed })]);
    poller.start(record);
    await flush();
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1000);
    await vi.advanceTimersByTimeAsync(1000);
    await flush();
    expect(hooks.onFinished).toHaveBeenCalledTimes(1);
    expect(poller.isPolling('r1')).toBe(false);
    await vi.advanceTimersByTimeAsync(5000);
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(3);
  });

  it('a single failed request does not stop polling', async () => {
    const { hooks, poller } = setup([new Error('blip'), status(), status()]);
    poller.start(record);
    await flush();
    await vi.advanceTimersByTimeAsync(1000);
    expect(hooks.onGaveUp).not.toHaveBeenCalled();
    expect(poller.isPolling('r1')).toBe(true);
    expect(hooks.onStatus).toHaveBeenCalled();
    poller.stopAll();
  });

  it('gives up after maxFailures consecutive failures', async () => {
    const { hooks, poller } = setup([new Error('down')]);
    poller.start(record);
    await flush();
    await vi.advanceTimersByTimeAsync(1000);
    await vi.advanceTimersByTimeAsync(1000);
    await flush();
    expect(hooks.onGaveUp).toHaveBeenCalledTimes(1);
    expect(poller.isPolling('r1')).toBe(false);
  });

  it('slows down while waiting for AI and speeds up again', async () => {
    const { hooks, poller } = setup([status({ waitingForAi: true }), status({ waitingForAi: true }), status()]);
    poller.start(record);
    await flush();
    await vi.advanceTimersByTimeAsync(1000);
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(1); // интервал уже 10 с
    await vi.advanceTimersByTimeAsync(10_000);
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(10_000); // этот ответ — уже не ждём ИИ, интервал снова 1 с
    await flush();
    const afterSpeedUp = hooks.fetchStatus.mock.calls.length;
    await vi.advanceTimersByTimeAsync(1000);
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(afterSpeedUp + 1);
    await vi.advanceTimersByTimeAsync(1000);
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(afterSpeedUp + 2);
    poller.stopAll();
  });

  it('stop() prevents further requests', async () => {
    const { hooks, poller } = setup([status()]);
    poller.start(record);
    await flush();
    poller.stop('r1');
    await vi.advanceTimersByTimeAsync(5000);
    expect(hooks.fetchStatus).toHaveBeenCalledTimes(1);
  });
});
