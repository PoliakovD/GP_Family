import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BreakpointService } from '../../services/breakpoint.service';
import { ActionMenuComponent } from './action-menu.component';

/** Мобильное меню «⋯»: действие, открывающее свой лист, не должно закрываться запоздавшим popstate шторки. */
describe('ActionMenuComponent — мобильная шторка', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({ providers: [{ provide: BreakpointService, useValue: { tier: () => 'narrow' } }] });
  });
  afterEach(() => {
    vi.useRealTimers();
    history.replaceState(null, '');
  });

  it('пока шторка снимает свою запись истории, действие ждёт popstate', () => {
    const f = TestBed.createComponent(ActionMenuComponent);
    const handler = vi.fn();
    f.componentRef.setInput('actions', [{ label: 'Справка', handler }]);
    f.componentInstance.open.set(true);
    history.replaceState({ overlayDismiss: true }, '');

    f.componentInstance.select({ label: 'Справка', handler });
    expect(f.componentInstance.open()).toBe(false);
    expect(handler).not.toHaveBeenCalled();

    window.dispatchEvent(new PopStateEvent('popstate'));
    expect(handler).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(500); // запасной таймер не вызывает действие второй раз
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('без записи шторки в истории действие выполняется сразу', () => {
    const f = TestBed.createComponent(ActionMenuComponent);
    const handler = vi.fn();
    f.componentRef.setInput('actions', [{ label: 'Удалить', handler }]);
    history.replaceState(null, '');
    f.componentInstance.select({ label: 'Удалить', handler });
    expect(handler).toHaveBeenCalledTimes(1);
  });
});
