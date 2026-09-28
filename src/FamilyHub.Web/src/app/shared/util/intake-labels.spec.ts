import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DoseScheduleMode, DoseUnit, FoodRelation, type DoseSchedule } from '../../models/types';
import {
  daysBetween, describePattern, formatUnits, progressText, toDateInput, todayLocal,
} from './intake-labels';

describe('todayLocal', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('formats the BROWSER-LOCAL date as yyyy-MM-dd, not UTC', () => {
    // Регрессия TECH_DEBT.md #18: this — что todayLocal() умышленно берёт локальную дату браузера,
    // а не UTC (бэкенд по этому же принципу сравнивает "сегодня" в поясе пользователя, см.
    // VaccinationService.TodayForAsync). 23:30 по местному времени должны остаться "сегодня",
    // даже если в UTC это уже следующий день.
    vi.setSystemTime(new Date(2026, 8, 27, 23, 30)); // локальный конструктор Date, не UTC-строка
    expect(todayLocal()).toBe('2026-09-27');
  });

  it('pads single-digit month and day', () => {
    vi.setSystemTime(new Date(2026, 0, 5));
    expect(todayLocal()).toBe('2026-01-05');
  });
});

describe('toDateInput', () => {
  it('formats an arbitrary Date the same way', () => {
    expect(toDateInput(new Date(2026, 11, 31))).toBe('2026-12-31');
  });
});

describe('daysBetween', () => {
  it('is inclusive of both endpoints', () => {
    expect(daysBetween('2026-01-01', '2026-01-01')).toBe(1);
    expect(daysBetween('2026-01-01', '2026-01-07')).toBe(7);
  });
});

describe('formatUnits', () => {
  it('formats a whole number without a decimal separator', () => {
    expect(formatUnits(1, DoseUnit.Tablet)).toBe('1 таб.');
  });

  it('formats a fractional number with a comma, not a dot', () => {
    expect(formatUnits(0.5, DoseUnit.Tablet)).toBe('0,5 таб.');
    expect(formatUnits(2.5, DoseUnit.Ml)).toBe('2,5 мл');
  });
});

describe('describePattern', () => {
  const base: DoseSchedule = { mode: DoseScheduleMode.TimesPerDay, times: [], intervalHours: null, intervalStart: null, intervalUnits: null, weekdays: null, cycleOnDays: null, cycleOffDays: null, maxPerDay: null };

  it('describes TimesPerDay with explicit times', () => {
    const s: DoseSchedule = { ...base, times: [{ at: '08:00:00', units: 1 }, { at: '20:00:00', units: 1 }] };
    expect(describePattern(s)).toBe('2 раза в день, в 8:00 и 20:00');
  });

  it('describes a cycle of 1 on / 1 off as "через день"', () => {
    const s: DoseSchedule = { ...base, mode: DoseScheduleMode.Cycle, cycleOnDays: 1, cycleOffDays: 1 };
    expect(describePattern(s)).toBe('через день');
  });

  it('describes AsNeeded with the daily cap', () => {
    const s: DoseSchedule = { ...base, mode: DoseScheduleMode.AsNeeded, maxPerDay: 3 };
    expect(describePattern(s)).toBe('по необходимости, не чаще 3 раз в сутки');
  });
});

describe('progressText', () => {
  it('reports an unstarted course', () => {
    expect(progressText(0, 10)).toBe('ещё не начался');
  });

  it('reports days for a short course (≤14 days)', () => {
    expect(progressText(4, 7)).toBe('день 4 из 7');
  });

  it('reports weeks, not days, once the course exceeds 14 days', () => {
    expect(progressText(10, 56)).toBe('неделя 2 из 8');
  });

  it('reports an indefinite course', () => {
    expect(progressText(10, null)).toBe('постоянно');
  });
});
