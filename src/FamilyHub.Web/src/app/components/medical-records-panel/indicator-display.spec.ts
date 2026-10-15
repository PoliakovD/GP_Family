import { describe, expect, it } from 'vitest';
import { IndicatorFlag, RefSource } from '../../models/types';
import type { IndicatorDto } from '../../models/types';
import {
  deviationAmount, deviationFor, indicatorReference, indicatorStatusGroup, indicatorSubtitle, isCalculatedRef,
  isInferredRef, scaleBounds,
  scaleValue, sortIndicators, groupIndicatorsByPanel, NO_PANEL_LABEL,
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

describe('groupIndicatorsByPanel', () => {
  it('no panels — single unlabeled group, order kept', () => {
    const groups = groupIndicatorsByPanel([ind({ id: 'a' }), ind({ id: 'b', panelLabel: null })]);
    expect(groups.length).toBe(1);
    expect(groups[0].label).toBeNull();
    expect(groups[0].items.map((i) => i.id)).toEqual(['a', 'b']);
  });

  it('consecutive indicators of one panel form a block; trailing unpaneled block is labeled', () => {
    const groups = groupIndicatorsByPanel([
      ind({ id: 'hgb', panelLabel: 'Общий анализ крови' }),
      ind({ id: 'wbc', panelLabel: 'Общий анализ крови' }),
      ind({ id: 'neu', panelLabel: 'Лейкоцитарная формула' }),
      ind({ id: 'fer', panelLabel: null }),
    ]);
    expect(groups.map((g) => g.label)).toEqual(['Общий анализ крови', 'Лейкоцитарная формула', NO_PANEL_LABEL]);
    expect(groups.map((g) => g.items.map((i) => i.id))).toEqual([['hgb', 'wbc'], ['neu'], ['fer']]);
  });

  it('leading unpaneled block has no heading', () => {
    const groups = groupIndicatorsByPanel([ind({ id: 'x' }), ind({ id: 'y', panelLabel: 'Биохимия' })]);
    expect(groups.map((g) => g.label)).toEqual([null, 'Биохимия']);
  });
});

describe('status groups', () => {
  it('ok / bad / none for unknown', () => {
    expect(indicatorStatusGroup(ind())).toBe('ok');
    expect(indicatorStatusGroup(ind({ flag: IndicatorFlag.Low }))).toBe('bad');
    expect(indicatorStatusGroup(ind({ flag: IndicatorFlag.Critical }))).toBe('bad');
    expect(indicatorStatusGroup(ind({ flag: IndicatorFlag.Unknown }))).toBe('none');
  });

  it('deviation amount: distance past the violated bound, null inside the norm or without numbers', () => {
    const bounds = { refLowText: '6.5', refHighText: '12.5' };
    expect(deviationAmount(ind({ ...bounds, valueNumericText: '4.24' }))).toBe('2,26');
    expect(deviationAmount(ind({ ...bounds, valueNumericText: '13' }))).toBe('0,5');
    expect(deviationAmount(ind({ ...bounds, valueNumericText: '8' }))).toBeNull();
    expect(deviationAmount(ind({ refLowText: null, refHighText: '5', valueNumericText: '7' }))).toBe('2');
    expect(deviationAmount(ind({ ...bounds, valueNumericText: null }))).toBeNull();
  });

  it('subtitle: printed name first, otherwise the textual norm when there is no scale', () => {
    expect(indicatorSubtitle(ind({ rawDisplayName: 'СОЭ по Панченкову', displayName: 'СОЭ' }))).toBe('СОЭ по Панченкову');
    expect(indicatorSubtitle(ind({ rawDisplayName: null, refLowText: '1', refHighText: '2' }))).toBeNull();
    expect(indicatorSubtitle(ind({ rawDisplayName: null, refLowText: null, refHighText: null, refText: 'отрицательно' }))).toBe('отрицательно');
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
