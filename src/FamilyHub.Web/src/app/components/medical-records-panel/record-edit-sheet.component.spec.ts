import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MedicalRecordKind } from '../../models/types';
import type { MedicalRecord } from '../../models/types';
import { ApiService } from '../../services/api.service';
import { toApiError } from '../../services/api-error';
import { ToastService } from '../../shared/toast/toast.service';
import { MEDICAL_RECORD_KIND_LABELS } from '../../shared/util/medical-record-labels';
import { RecordEditSheetComponent } from './record-edit-sheet.component';

const analysis = {
  id: 'r1', kind: MedicalRecordKind.Analysis, title: 'ОАК', recordDate: '2026-09-01', doctor: null,
  description: null, specimenKbId: 'blood', specimenDisplayName: 'Кровь', personName: 'Иван',
} as unknown as MedicalRecord;

describe('RecordEditSheetComponent', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let toastError: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    api = {
      getSpecimens: vi.fn().mockResolvedValue([]),
      searchSpecimens: vi.fn().mockResolvedValue([]),
      createSpecimen: vi.fn(),
      updateMedicalRecord: vi.fn().mockResolvedValue(undefined),
      setRecordSpecimen: vi.fn().mockResolvedValue(undefined),
      setPendingSpecimen: vi.fn().mockResolvedValue(undefined),
    };
    toastError = vi.fn();
    TestBed.configureTestingModule({
      imports: [RecordEditSheetComponent],
      providers: [
        { provide: ApiService, useValue: api },
        { provide: ToastService, useValue: { error: toastError, info: vi.fn(), success: vi.fn() } },
      ],
    });
  });

  function create(hint: string | null = null) {
    const fixture = TestBed.createComponent(RecordEditSheetComponent);
    fixture.componentRef.setInput('record', analysis);
    fixture.componentRef.setInput('specimenHint', hint);
    fixture.componentRef.setInput('labels', MEDICAL_RECORD_KIND_LABELS[MedicalRecordKind.Analysis]);
    fixture.componentRef.setInput('doctorsDatalistId', 'doctors');
    fixture.componentInstance.ngOnInit();
    return fixture.componentInstance;
  }

  it('prefills the form from the record (hint wins for specimen)', () => {
    expect(create().specimenQuery).toBe('Кровь');
    const withHint = create('мазок');
    expect(withHint.specimenQuery).toBe('мазок');
    expect(withHint.form.recordDate).toBe('2026-09-01');
  });

  it('saves fields without touching the specimen when it did not change', async () => {
    const sheet = create();
    const saved = vi.fn();
    sheet.saved.subscribe(saved);
    sheet.form.doctor = '  Петрова ';
    await (sheet as unknown as { save: () => Promise<void> }).save();
    expect(api['updateMedicalRecord']).toHaveBeenCalledWith('r1', expect.objectContaining({ doctor: 'Петрова', title: 'ОАК' }));
    expect(api['setRecordSpecimen']).not.toHaveBeenCalled();
    expect(saved).toHaveBeenCalledWith(null);
  });

  it('a new specimen is registered and applied', async () => {
    api['createSpecimen'].mockResolvedValue({ specimenKbId: 'urine', displayName: 'Моча' });
    const sheet = create();
    const saved = vi.fn();
    sheet.saved.subscribe(saved);
    sheet.specimenQuery = 'Моча';
    await (sheet as unknown as { save: () => Promise<void> }).save();
    expect(api['setRecordSpecimen']).toHaveBeenCalledWith('r1', 'urine');
    expect(saved).toHaveBeenCalledWith(null);
  });

  it('AI unavailable — specimen goes to pending and the parent gets an info message', async () => {
    api['createSpecimen'].mockRejectedValue(toApiError(new HttpErrorResponse({ status: 503 })));
    const sheet = create();
    const saved = vi.fn();
    sheet.saved.subscribe(saved);
    sheet.specimenQuery = 'Мазок из зева';
    await (sheet as unknown as { save: () => Promise<void> }).save();
    expect(api['setPendingSpecimen']).toHaveBeenCalledWith('r1', 'Мазок из зева');
    expect(saved.mock.calls[0][0]).toContain('будет проверен');
  });

  it('a rejected specimen blocks saving', async () => {
    api['createSpecimen'].mockRejectedValue(toApiError(new HttpErrorResponse({ status: 422, error: { code: 'rejected', reason: 'Это не биоматериал.' } })));
    const sheet = create();
    const saved = vi.fn();
    sheet.saved.subscribe(saved);
    sheet.specimenQuery = 'Котик';
    await (sheet as unknown as { save: () => Promise<void> }).save();
    expect(sheet.specimenError).toBe('Это не биоматериал.');
    expect(api['updateMedicalRecord']).not.toHaveBeenCalled();
    expect(saved).not.toHaveBeenCalled();
  });
});
