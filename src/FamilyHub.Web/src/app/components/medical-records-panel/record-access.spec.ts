import { describe, expect, it } from 'vitest';
import type { MedicalRecord } from '../../models/types';
import { accessSummary, isOnlyMe, isVisibleToFamily } from './record-access';

const record = (hidden: string[]) => ({ hiddenFamilyIds: hidden } as unknown as MedicalRecord);

describe('record access', () => {
  it('visible only with a family share and no per-record hide', () => {
    expect(isVisibleToFamily(record([]), ['f1'], 'f1')).toBe(true);
    expect(isVisibleToFamily(record(['f1']), ['f1'], 'f1')).toBe(false);
    expect(isVisibleToFamily(record([]), [], 'f1')).toBe(false);
  });

  it('only me — no shares at all, or hidden from every shared family', () => {
    expect(isOnlyMe(record([]), [])).toBe(true);
    expect(isOnlyMe(record(['f1', 'f2']), ['f1', 'f2'])).toBe(true);
    expect(isOnlyMe(record(['f1']), ['f1', 'f2'])).toBe(false);
  });

  it('summary text', () => {
    expect(accessSummary(record([]), [])).toBe('Только вы');
    expect(accessSummary(record([]), ['f1', 'f2'])).toBe('Все семьи');
    expect(accessSummary(record(['f2']), ['f1', 'f2'])).toBe('Все семьи, кроме 1');
    expect(accessSummary(record(['f1', 'f2']), ['f1', 'f2'])).toBe('Только вы');
  });
});
