import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { IndicatorFlag, RefSource } from '../../models/types';
import type { IndicatorDto } from '../../models/types';
import { ApiService } from '../../services/api.service';
import { ToastService } from '../../shared/toast/toast.service';
import { IndicatorTableComponent } from './indicator-table.component';

const ind = (id: string, name: string, flag: number): IndicatorDto => ({
  id, analyteKey: id, displayName: name, flag, refSource: RefSource.Blank, specimenKbId: 's',
  specimenDisplayName: null, position: 0, valueRaw: '1', unit: null, refLowText: '0', refHighText: '2',
  refText: null, recordDate: '2026-01-01', medicalRecordId: 'r1', valueNumericText: '1', kbAnalyteId: null,
  rawDisplayName: null, enrichmentPending: false, enrichmentQueueAhead: 0,
  enrichmentWaitingForAi: false,
} as IndicatorDto);

describe('IndicatorTableComponent', () => {
  let api: { createIndicator: ReturnType<typeof vi.fn> };
  let toastError: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    api = { createIndicator: vi.fn().mockResolvedValue({}) };
    toastError = vi.fn();
    TestBed.configureTestingModule({
      imports: [IndicatorTableComponent],
      providers: [
        { provide: ApiService, useValue: api },
        { provide: ToastService, useValue: { error: toastError } },
      ],
    });
  });

  function create(indicators: IndicatorDto[], wide = true) {
    const fixture = TestBed.createComponent(IndicatorTableComponent);
    fixture.componentRef.setInput('recordId', 'r1');
    fixture.componentRef.setInput('indicators', indicators);
    fixture.componentRef.setInput('wide', wide);
    fixture.detectChanges();
    return fixture;
  }

  it('renders abnormal first by default and re-sorts on demand', () => {
    const fixture = create([ind('a', 'Альбумин', IndicatorFlag.Normal), ind('b', 'Билирубин', IndicatorFlag.High)]);
    const rows = () => Array.from(fixture.nativeElement.querySelectorAll('tr.indicator-row')).map((r) => (r as HTMLElement).textContent!.trim());
    expect(rows()[0]).toContain('Билирубин');
    fixture.componentInstance.sortMode.set('alpha');
    fixture.detectChanges();
    expect(rows()[0]).toContain('Альбумин');
  });

  it('clicking a row emits open with the indicator', () => {
    const fixture = create([ind('a', 'Альбумин', IndicatorFlag.Normal)]);
    const opened = vi.fn();
    fixture.componentInstance.open.subscribe(opened);
    (fixture.nativeElement.querySelector('tr.indicator-row') as HTMLElement).click();
    expect(opened).toHaveBeenCalledWith(expect.objectContaining({ id: 'a' }));
  });

  it('panel subheadings appear only in «Как в бланке» mode', () => {
    const fixture = create([
      { ...ind('h', 'Гемоглобин', IndicatorFlag.Normal), panelLabel: 'Общий анализ крови' },
      { ...ind('n', 'Нейтрофилы', IndicatorFlag.High), panelLabel: 'Лейкоцитарная формула' },
    ]);
    const headings = () => Array.from(fixture.nativeElement.querySelectorAll('tr.indicator-panel-row'))
      .map((r) => (r as HTMLElement).textContent!.trim());
    expect(headings()).toEqual([]);
    fixture.componentInstance.sortMode.set('form');
    fixture.detectChanges();
    expect(headings()).toEqual(['Общий анализ крови', 'Лейкоцитарная формула']);
  });

  it('narrow screens show panel headings above card groups', () => {
    const fixture = create([{ ...ind('n', 'Нейтрофилы', IndicatorFlag.High), panelLabel: 'Лейкоцитарная формула' }], false);
    fixture.componentInstance.sortMode.set('form');
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('.indicator-panel-heading') as HTMLElement).textContent!.trim())
      .toBe('Лейкоцитарная формула');
  });

  it('narrow screens get cards instead of a table', () => {
    const fixture = create([ind('a', 'Альбумин', IndicatorFlag.Normal)], false);
    expect(fixture.nativeElement.querySelector('table')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.indicator-card').length).toBe(1);
  });

  it('adding a new indicator calls the API with a sanitized form and emits created', async () => {
    const fixture = create([]);
    const created = vi.fn();
    fixture.componentInstance.created.subscribe(created);
    fixture.componentInstance.startCreate();
    fixture.componentInstance.form = { displayName: ' Ферритин ', valueRaw: ' 40 ', unit: ' ', refLowText: null, refHighText: null, refText: null };
    await fixture.componentInstance.saveNew();
    expect(api.createIndicator).toHaveBeenCalledWith('r1', expect.objectContaining({ displayName: 'Ферритин', valueRaw: '40', unit: null }));
    expect(created).toHaveBeenCalled();
    expect(fixture.componentInstance.creating).toBe(false);
  });

  it('an empty name is rejected with a message', async () => {
    const fixture = create([]);
    fixture.componentInstance.startCreate();
    await fixture.componentInstance.saveNew();
    expect(toastError).toHaveBeenCalledWith('Укажите название показателя.');
    expect(api.createIndicator).not.toHaveBeenCalled();
  });
});
