import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { House } from 'lucide-angular';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FabComponent } from './fab/fab.component';
import { FieldDirective } from './field/field.directive';
import { IconComponent } from './icon/icon.component';
import { PillTabsComponent } from './pill-tabs/pill-tabs.component';
import { StatFilterComponent } from './stat-filter/stat-filter.component';
import { TaskRowComponent } from './task-row/task-row.component';

/** Доступность базовых компонентов (DESIGN §1 п.5, §3): aria-атрибуты, без которых компонент не принимается. */
describe('shared/ui — доступность', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('icon без подписи скрыт от AT, с подписью — role="img" и aria-label', () => {
    const f = TestBed.createComponent(IconComponent);
    f.componentRef.setInput('icon', House);
    f.detectChanges();
    const el: HTMLElement = f.nativeElement;
    expect(el.getAttribute('aria-hidden')).toBe('true');
    expect(el.querySelector('svg')?.getAttribute('stroke-width')).toBe('1.8');

    f.componentRef.setInput('label', 'Главная');
    f.detectChanges();
    expect(el.getAttribute('aria-hidden')).toBeNull();
    expect(el.getAttribute('role')).toBe('img');
    expect(el.getAttribute('aria-label')).toBe('Главная');
  });

  it('fab — кнопка с обязательным aria-label', () => {
    const f = TestBed.createComponent(FabComponent);
    f.componentRef.setInput('label', 'Добавить запись');
    f.detectChanges();
    const btn: HTMLButtonElement = f.nativeElement.querySelector('button');
    expect(btn.getAttribute('aria-label')).toBe('Добавить запись');
    const spy = vi.fn();
    f.componentInstance.activated.subscribe(spy);
    btn.click();
    expect(spy).toHaveBeenCalledOnce();
  });

  it('stat-filter переключает aria-pressed', () => {
    const f = TestBed.createComponent(StatFilterComponent);
    f.componentRef.setInput('value', 6);
    f.componentRef.setInput('label', 'вне нормы');
    f.detectChanges();
    const btn: HTMLButtonElement = f.nativeElement.querySelector('button');
    expect(btn.getAttribute('aria-pressed')).toBe('false');
    btn.click();
    f.detectChanges();
    expect(btn.getAttribute('aria-pressed')).toBe('true');
  });

  it('pill-tabs — tablist/tab, aria-selected и роуминг-tabindex; стрелка переключает вкладку', () => {
    const f = TestBed.createComponent(PillTabsComponent);
    f.componentRef.setInput('tabs', [{ id: 'chat', label: 'Чат', count: 3 }, { id: 'members', label: 'Участники' }]);
    f.componentRef.setInput('value', 'chat');
    f.detectChanges();
    const host: HTMLElement = f.nativeElement;
    expect(host.getAttribute('role')).toBe('tablist');
    const tabs = host.querySelectorAll<HTMLButtonElement>('[role="tab"]');
    expect(tabs[0].getAttribute('aria-selected')).toBe('true');
    expect(tabs[0].tabIndex).toBe(0);
    expect(tabs[1].tabIndex).toBe(-1);

    host.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    f.detectChanges();
    expect(f.componentInstance.value()).toBe('members');
    expect(tabs[1].getAttribute('aria-selected')).toBe('true');
  });

  it('task-row без ссылки: мобильный шеврон — кнопка с понятным aria-label', () => {
    const f = TestBed.createComponent(TaskRowComponent);
    f.componentRef.setInput('icon', House);
    f.componentRef.setInput('title', 'Прививка от гриппа');
    f.componentRef.setInput('actionLabel', 'Отметить');
    f.detectChanges();
    const chevron: HTMLButtonElement = f.nativeElement.querySelector('.tr-chevron-btn');
    expect(chevron.getAttribute('aria-label')).toBe('Отметить: Прививка от гриппа');
  });
});

@Component({
  standalone: true,
  imports: [FieldDirective],
  template: `
    <div appField [invalid]="invalid()">
      <label>Почта</label>
      <input class="input" />
      <span class="field-hint field-hint-muted">Подсказка</span>
      @if (invalid()) { <span class="field-hint field-hint-error">Проверьте адрес</span> }
    </div>
  `,
})
class FieldHostComponent {
  readonly invalid = signal(false);
}

describe('appField', () => {
  it('связывает label, подсказки и aria-invalid с контролом', () => {
    const f = TestBed.createComponent(FieldHostComponent);
    f.detectChanges();
    const input: HTMLInputElement = f.nativeElement.querySelector('input');
    const label: HTMLLabelElement = f.nativeElement.querySelector('label');
    expect(input.id).toBeTruthy();
    expect(label.htmlFor).toBe(input.id);
    expect(input.getAttribute('aria-describedby')?.split(' ')).toHaveLength(1);
    expect(input.hasAttribute('aria-invalid')).toBe(false);

    f.componentInstance.invalid.set(true);
    f.detectChanges();
    expect(input.getAttribute('aria-invalid')).toBe('true');
    const ids = input.getAttribute('aria-describedby')!.split(' ');
    expect(ids).toHaveLength(2);
    const error = f.nativeElement.querySelector('.field-hint-error') as HTMLElement;
    expect(ids).toContain(error.id);
    expect(error.getAttribute('role')).toBe('alert');
  });
});
