import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ExtractionJobStatus, ExtractionStage, ExtractionStatus, MedicalRecordKind } from '../../models/types';
import type { ExtractionStatusResponse, MedicalRecord } from '../../models/types';
import { RecordDetailHeaderComponent } from './record-detail-header.component';
import { RecordListCardComponent } from './record-list-card.component';
import { RecordRecognitionStatusComponent } from './record-recognition-status.component';
import { recordShortName, unknownIndicatorCount } from './record-display';

const record = (over: Partial<MedicalRecord> = {}): MedicalRecord => ({
  id: 'r1', kind: MedicalRecordKind.Analysis, title: null, recordDate: '2026-09-01', doctor: null,
  description: null, personName: 'Иванов Иван', ownerUserId: 'u1', targetUserId: null, familyDependentId: null,
  attachmentCount: 2, indicatorCount: 5, abnormalIndicatorCount: 1, normalIndicatorCount: 3,
  unrecognizedAttachmentCount: 0, extractionStatus: ExtractionStatus.Ready, kindIsAutoDetected: false,
  specimenKbId: 'blood', specimenDisplayName: 'Кровь', hiddenFamilyIds: [],
  ...over,
} as unknown as MedicalRecord);

const text = (el: HTMLElement) => el.textContent!.replace(/\s+/g, ' ');

describe('record-display', () => {
  it('short name falls back to the record kind', () => {
    expect(recordShortName(record())).toBe('Анализ');
    expect(recordShortName(record({ kind: MedicalRecordKind.DoctorVisit } as never))).toBe('Приём врача');
    expect(recordShortName(record({ title: 'ОАК' } as never))).toBe('ОАК');
  });

  it('unknown indicators are the remainder and never negative', () => {
    expect(unknownIndicatorCount(record())).toBe(1);
    expect(unknownIndicatorCount(record({ indicatorCount: 1 } as never))).toBe(0);
  });
});

describe('record UI components', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('list card shows chips and emits open on row click', () => {
    const fixture = TestBed.createComponent(RecordListCardComponent);
    fixture.componentRef.setInput('record', record({ doctor: 'Петрова' } as never));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('2 файла');
    expect(text(el)).toContain('1 вне нормы');
    expect(text(el)).toContain('Врач: Петрова');
    const opened = vi.fn();
    fixture.componentInstance.open.subscribe(opened);
    (el.querySelector('.record-card-row') as HTMLElement).click();
    expect(opened).toHaveBeenCalled();
  });

  it('recognition block: button, progress hint, partial and failure banners', () => {
    const fixture = TestBed.createComponent(RecordRecognitionStatusComponent);
    fixture.componentRef.setInput('record', record({ unrecognizedAttachmentCount: 1, extractionStatus: ExtractionStatus.Failed } as never));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Распознать');
    expect(text(el)).toContain('Не удалось распознать документ в прошлый раз');

    fixture.componentRef.setInput('steps', [{ id: 's', label: 'Распознаём текст', state: 'active' }]);
    fixture.componentRef.setInput('recognizing', true);
    fixture.detectChanges();
    expect(text(el)).toContain('Обычно это занимает 1–3 минуты');
    expect(text(el)).not.toContain('в прошлый раз');

    const partial: ExtractionStatusResponse = {
      status: ExtractionJobStatus.Completed, stage: ExtractionStage.Summarizing, indicatorCount: 3, error: 'часть не прочитана',
      totalFiles: 1, processedFiles: 1, createdAt: '', completedAt: null, queuePosition: 0, currentThought: null, waitingForAi: false,
    };
    fixture.componentRef.setInput('status', partial);
    fixture.detectChanges();
    expect(text(el)).toContain('Распознано не всё');
  });

  it('detail header: back only while loading; owner actions only for the owner', () => {
    const fixture = TestBed.createComponent(RecordDetailHeaderComponent);
    fixture.componentRef.setInput('backLabel', 'Анализы');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.record-detail-actions')).toBeNull();

    fixture.componentRef.setInput('record', record());
    fixture.componentRef.setInput('accessLabel', 'Только вы');
    fixture.detectChanges();
    expect(el.querySelector('[aria-label="Удалить"]')).toBeNull();
    expect(text(el)).toContain('Только вы');
    expect(text(el)).toContain('без нормы в бланке');

    fixture.componentRef.setInput('canEdit', true);
    fixture.detectChanges();
    const removed = vi.fn();
    fixture.componentInstance.remove.subscribe(removed);
    (el.querySelector('[aria-label="Удалить"]') as HTMLElement).click();
    expect(removed).toHaveBeenCalled();
  });
});
