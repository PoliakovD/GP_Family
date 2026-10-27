import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LandingDemoComponent } from './landing-demo.component';

@Component({
  standalone: true,
  imports: [LandingDemoComponent],
  template: `
    <app-landing-demo kicker="Фича" heading="Заголовок" [steps]="steps">
      <ng-template #mock let-s let-d="done"><span class="probe">{{ s }}:{{ d }}</span></ng-template>
    </app-landing-demo>
  `,
})
class DemoHostComponent {
  readonly steps = [
    { title: 'Первый', text: 'Текст 1' },
    { title: 'Второй', text: 'Текст 2' },
    { title: 'Третий', text: 'Текст 3' },
  ];
}

/** Демо лендинга: шаги — настоящие кнопки, макет скрыт от AT; при reduced-motion сразу итог шага, без курсора. */
describe('LandingDemoComponent', () => {
  beforeEach(() => {
    // Узкий экран + reduced-motion: без автопроигрывания по таймеру и без анимации курсора.
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: q.includes('reduce'), media: q, addEventListener: () => {}, removeEventListener: () => {},
    }));
    vi.stubGlobal('IntersectionObserver', class { observe() {} disconnect() {} });
  });
  afterEach(() => vi.unstubAllGlobals());

  it('касание шага показывает его итоговое состояние; макет — aria-hidden и inert', async () => {
    const f = TestBed.createComponent(DemoHostComponent);
    f.detectChanges();
    await f.whenStable();
    f.detectChanges(); // afterNextRender уже отработал — показать первый шаг
    const el: HTMLElement = f.nativeElement;

    expect(el.querySelector('.probe')!.textContent).toBe('0:true');
    expect(el.querySelector('.ldd-cursor')).toBeNull();
    const phone = el.querySelector('.ldd-phone')!;
    expect(phone.getAttribute('aria-hidden')).toBe('true');
    expect(phone.hasAttribute('inert')).toBe(true);

    const buttons = el.querySelectorAll<HTMLButtonElement>('.ldd-step-btn');
    expect(buttons[0].getAttribute('aria-pressed')).toBe('true');
    buttons[2].click();
    f.detectChanges();
    expect(buttons[2].getAttribute('aria-pressed')).toBe('true');
    expect(buttons[0].getAttribute('aria-pressed')).toBe('false');
    expect(el.querySelector('.probe')!.textContent).toBe('2:true');
    expect(el.querySelector('h2')!.textContent).toBe('Заголовок');
  });
});
