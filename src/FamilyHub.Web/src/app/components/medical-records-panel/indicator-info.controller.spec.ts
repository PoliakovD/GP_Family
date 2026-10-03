import { beforeEach, describe, expect, it, vi } from 'vitest';
import { convertToParamMap } from '@angular/router';
import { IndicatorFlag, RefSource } from '../../models/types';
import type { IndicatorDto } from '../../models/types';
import { IndicatorInfoController, type IndicatorInfoDeps } from './indicator-info.controller';

const ind = (id: string, over: Partial<IndicatorDto> = {}): IndicatorDto => ({
  id, analyteKey: id, displayName: 'Гемоглобин', flag: IndicatorFlag.Normal, refSource: RefSource.Blank,
  specimenKbId: 's', specimenDisplayName: null, position: 0, valueRaw: '130', unit: 'г/л', refLowText: '120',
  refHighText: '160', refText: null, recordDate: '2026-01-01', medicalRecordId: 'r1', valueNumericText: '130',
  kbAnalyteId: null, rawDisplayName: null, enrichmentPending: false, enrichmentLiveText: null,
  enrichmentQueueAhead: 0, enrichmentWaitingForAi: false, ...over,
} as IndicatorDto);

describe('IndicatorInfoController', () => {
  let deps: IndicatorInfoDeps & { api: Record<string, ReturnType<typeof vi.fn>> };
  let wide: boolean;
  let query: Record<string, string>;
  let navigate: ReturnType<typeof vi.fn>;
  let changed: ReturnType<typeof vi.fn>;
  let confirmResult: boolean;

  beforeEach(() => {
    wide = false;
    query = {};
    navigate = vi.fn();
    changed = vi.fn();
    confirmResult = true;
    deps = {
      api: {
        getIndicatorArticle: vi.fn().mockResolvedValue({ article: { id: 'kb1' }, patient: null, matchedRefRangeIndex: 0, historyAvailable: false }),
        getRecordIndicatorHistory: vi.fn(),
        getKbAnalyte: vi.fn().mockResolvedValue({ id: 'kb2' }),
        updateIndicator: vi.fn().mockResolvedValue(undefined),
        deleteIndicator: vi.fn().mockResolvedValue(undefined),
        getRecordIndicators: vi.fn().mockResolvedValue([ind('i1', { valueRaw: '140' })]),
      },
      router: { navigate } as never,
      route: { get snapshot() { return { queryParamMap: convertToParamMap(query) }; } } as never,
      toast: { error: vi.fn() } as never,
      confirm: { confirm: vi.fn(async () => confirmResult) } as never,
      isWide: () => wide,
      recordId: () => 'r1',
      indicatorsOf: () => [ind('i1')],
      onIndicatorsChanged: changed,
    } as never;
  });

  it('opens an indicator: reading right away, article after load; URL only on narrow screens', async () => {
    const c = new IndicatorInfoController(deps);
    const pending = c.openIndicator(ind('i1'));
    expect(c.open).toBe(true);
    expect(c.reading?.valueNumeric).toBe(130);
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { indicator: 'i1' } }));
    await pending;
    expect(c.card).toEqual({ id: 'kb1' });
    expect(c.loading).toBe(false);

    wide = true;
    navigate.mockClear();
    await c.openIndicator(ind('i1'));
    expect(navigate).not.toHaveBeenCalled();
  });

  it('related analyte opens without personal context', async () => {
    const c = new IndicatorInfoController(deps);
    await c.openIndicator(ind('i1'));
    await c.openRelated('kb2');
    expect(c.indicator).toBeNull();
    expect(c.reading).toBeNull();
    expect(c.card).toEqual({ id: 'kb2' });
  });

  it('syncFromRoute opens/closes by ?indicator= on narrow screens', async () => {
    const c = new IndicatorInfoController(deps);
    query = { indicator: 'i1' };
    c.syncFromRoute();
    expect(c.open).toBe(true);
    query = {};
    c.syncFromRoute();
    expect(c.open).toBe(false);
  });

  it('saving an edit reloads indicators, notifies the parent and refreshes the open article', async () => {
    const c = new IndicatorInfoController(deps);
    await c.openIndicator(ind('i1'), false);
    c.startEdit(ind('i1'));
    c.editForm.valueRaw = ' 140 ';
    await c.saveEdit('r1');
    expect(deps.api['updateIndicator']).toHaveBeenCalledWith('i1', expect.objectContaining({ valueRaw: '140' }));
    expect(changed).toHaveBeenCalledWith('r1', expect.any(Array));
    expect(c.editingId).toBeNull();
    expect(c.reading?.valueRaw).toBe('140');
  });

  it('delete asks for confirmation and closes the open indicator', async () => {
    const c = new IndicatorInfoController(deps);
    await c.openIndicator(ind('i1'), false);
    confirmResult = false;
    await c.delete('r1', ind('i1'));
    expect(deps.api['deleteIndicator']).not.toHaveBeenCalled();
    confirmResult = true;
    await c.delete('r1', ind('i1'));
    expect(deps.api['deleteIndicator']).toHaveBeenCalledWith('i1');
    expect(c.open).toBe(false);
    expect(changed).toHaveBeenCalled();
  });
});
