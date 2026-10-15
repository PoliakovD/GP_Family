import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });
  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  it('по умолчанию — системная тема без data-theme', () => {
    const s = new ThemeService();
    expect(s.preference()).toBe('system');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('явный выбор ставит data-theme и запоминается на устройстве', () => {
    new ThemeService().set('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(new ThemeService().preference()).toBe('dark');
  });

  it('возврат к системной снимает атрибут и забывает выбор', () => {
    const s = new ThemeService();
    s.set('light');
    s.set('system');
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false);
    expect(localStorage.getItem('familyhub:theme')).toBeNull();
  });
});
