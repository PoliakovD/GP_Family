import { describe, expect, it } from 'vitest';
import { IndicatorFlag, RefSource } from '../../models/types';
import type { IndicatorDto } from '../../models/types';
import {
  deviationFor, flagClass, indicatorReference, isCalculatedRef, isInferredRef, rowStatusClass, scaleBounds,
  scaleValue, sortIndicators,
} from './indicator-display';

const ind = (over: Partial<IndicatorDto> = {}): IndicatorDto => ({
  id: 'i', analyteKey: 'k', displayName: 'Гемоглобин', flag: IndicatorFlag.Normal, refSource: RefSource.Blank,
  specimenKbId: 's', specimenDisplayName: null, position: 0, valueRaw: '130', unit: 'г/л',
  refLowText: '120', refHighText: '160', refText: null, recordDate: '2026-01-01', medicalRecordId: 'r',
  valueNumericText: '130', kbAnalyteId: null, rawDisplayName: null, enrichmentPending: false,
  enrichmentQueueAhead: 0, enrichmentWaitingForAi: false,
  ...over,
} as IndicatorDto);

describe('sortIndicators', () => {
  const a = ind({ id: 'a', displayName: 'Альбумин', flag: IndicatorFlag.Normal });
  const b = ind({ id: 'b', displayName: 'Билирубин', flag: IndicatorFlag.High });
  const c = ind({ id: 'c', displayName: 'Ведро', flag: IndicatorFlag.Normal });
  const d = ind({ id: 'd', displayName: 'Глюкоза', flag: IndicatorFlag.Low });

  it('abnormal first, keeping form order inside groups', () => {
    expect(sortIndicators([a, b, c, d], 'abnormal').map((i) => i.id)).toEqual(['b', 'd', 'a', 'c']);
  });

  it('form keeps server order; alpha sorts by label', () => {
    expect(sortIndicators([c, a, b], 'form').map((i) => i.id)).toEqual(['c', 'a', 'b']);
    expect(sortIndicators([c, a, b], 'alpha').map((i) => i.id)).toEqual(['a', 'b', 'c']);
  });

  it('does not mutate the input', () => {
    const input = [c, a];
    sortIndicators(input, 'alpha');
    expect(input.map((i) => i.id)).toEqual(['c', 'a']);
  });
});

describe('status classes', () => {
  it('row: ok / bad / none for unknown', () => {
    expect(rowStatusClass(ind())).toBe('indicator-row-ok');
    expect(rowStatusClass(ind({ flag: IndicatorFlag.Critical }))).toBe('indicator-row-bad');
    expect(rowStatusClass(ind({ flag: IndicatorFlag.Unknown }))).toBe('');
  });

  it('value cell', () => {
    expect(flagClass(IndicatorFlag.Low)).toBe('indicator-flag-warning');
    expect(flagClass(IndicatorFlag.Critical)).toBe('indicator-flag-danger');
    expect(flagClass(IndicatorFlag.Normal)).toBe('indicator-flag-ok');
    expect(flagClass(IndicatorFlag.Unknown)).toBe('indicator-flag-unknown');
  });
});

describe('reference and scale', () => {
  it('reference text variants', () => {
    expect(indicatorReference(ind())).toBe('120–160');
    expect(indicatorReference(ind({ refLowText: null }))).toBe('< 160');
    expect(indicatorReference(ind({ refHighText: null }))).toBe('> 120');
    expect(indicatorReference(ind({ refText: 'отрицательно' }))).toBe('отрицательно');
    expect(indicatorReference(ind({ refLowText: null, refHighText: null }))).toBeNull();
  });

  it('scale only with both numeric bounds', () => {
    expect(scaleBounds(ind())).toEqual({ low: 120, high: 160 });
    expect(scaleBounds(ind({ refHighText: null }))).toBeNull();
    expect(scaleValue(ind({ valueNumericText: null }))).toBeNull();
  });

  it('deviation text comes from the shared formatter', () => {
    expect(deviationFor(ind({ valueNumericText: '130' }), { low: 120, high: 160 })).toBeNull();
    expect(deviationFor(ind({ valueNumericText: '170' }), { low: 120, high: 160 })).toContain('выше');
  });

  it('AI reference badges', () => {
    expect(isCalculatedRef(ind({ refSource: RefSource.KbCalculated }))).toBe(true);
    expect(isInferredRef(ind({ refSource: RefSource.Inferred }))).toBe(true);
    expect(isInferredRef(ind())).toBe(false);
  });
});
