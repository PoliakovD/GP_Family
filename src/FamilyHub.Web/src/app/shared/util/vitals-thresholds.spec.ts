import { describe, expect, it } from 'vitest';
import { vitalsWarning } from './vitals-thresholds';

describe('vitalsWarning', () => {
  it('stable 160/100 is flagged even if it is the user\'s usual range', () => {
    expect(vitalsWarning('blood_pressure', 160, 100)).toContain('выше рекомендуемого');
  });

  it('diastolic alone ≥ 90 is enough', () => {
    expect(vitalsWarning('blood_pressure', 130, 92)).toContain('выше');
  });

  it('120/80 → no warning', () => {
    expect(vitalsWarning('blood_pressure', 120, 80)).toBeNull();
  });

  it('low pressure 85/55 → below', () => {
    expect(vitalsWarning('blood_pressure', 85, 55)).toContain('ниже');
  });

  it('pulse thresholds', () => {
    expect(vitalsWarning('pulse', 110, null)).toContain('выше 100');
    expect(vitalsWarning('pulse', 45, null)).toContain('ниже 50');
    expect(vitalsWarning('pulse', 72, null)).toBeNull();
  });

  it('other metrics are not judged', () => {
    expect(vitalsWarning('weight', 150, null)).toBeNull();
  });
});
