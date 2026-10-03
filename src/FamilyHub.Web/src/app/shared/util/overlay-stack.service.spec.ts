import { describe, expect, it } from 'vitest';
import { OverlayStackService } from './overlay-stack.service';

const esc = () => new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });

describe('OverlayStackService', () => {
  it('only the top overlay claims Escape', () => {
    const stack = new OverlayStackService();
    const form = {};
    const confirm = {};
    stack.open(form);
    stack.open(confirm);
    const e = esc();
    expect(stack.claimEscape(form, e)).toBe(false);
    expect(stack.claimEscape(confirm, e)).toBe(true);
  });

  it('the same keypress is not claimed twice even after the top closes synchronously', () => {
    const stack = new OverlayStackService();
    const form = {};
    const confirm = {};
    stack.open(form);
    stack.open(confirm);
    const e = esc();
    expect(stack.claimEscape(confirm, e)).toBe(true);
    stack.close(confirm);
    expect(stack.claimEscape(form, e)).toBe(false);
    expect(stack.claimEscape(form, esc())).toBe(true);
  });

  it('re-opening moves an overlay to the top', () => {
    const stack = new OverlayStackService();
    const a = {};
    const b = {};
    stack.open(a);
    stack.open(b);
    stack.open(a);
    expect(stack.claimEscape(a, esc())).toBe(true);
    expect(stack.hasOpen()).toBe(true);
    stack.close(a);
    stack.close(b);
    expect(stack.hasOpen()).toBe(false);
  });
});
