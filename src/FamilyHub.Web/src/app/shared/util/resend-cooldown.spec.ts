import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ResendCooldown } from './resend-cooldown';

describe('ResendCooldown', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('counts down and becomes ready', () => {
    const c = new ResendCooldown();
    expect(c.ready()).toBe(true);
    c.start(3);
    expect(c.ready()).toBe(false);
    expect(c.label()).toBe('0:03');
    vi.advanceTimersByTime(2000);
    expect(c.label()).toBe('0:01');
    vi.advanceTimersByTime(1000);
    expect(c.ready()).toBe(true);
  });

  it('formats minutes', () => {
    const c = new ResendCooldown();
    c.start(75);
    expect(c.label()).toBe('1:15');
    c.stop();
  });
});
