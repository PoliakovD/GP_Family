import { describe, expect, it } from 'vitest';
import { NotificationRelatedKind } from '../../models/types';
import { relatedEntityRoute } from './related-kind-route';

describe('relatedEntityRoute', () => {
  it('ведёт к самой сущности, если у неё есть экран', () => {
    expect(relatedEntityRoute(NotificationRelatedKind.MedicalRecordAnalysis, 'r1')).toEqual(['/health/records', 'r1']);
  });

  it('отчёт для врача открывает общий список отчётов — отдельного экрана у отчёта нет', () => {
    expect(relatedEntityRoute(NotificationRelatedKind.DoctorReport, 'rep1')).toEqual(['/health/reports']);
  });
});
