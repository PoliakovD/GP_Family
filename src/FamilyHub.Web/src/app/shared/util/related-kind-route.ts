import { NotificationRelatedKind } from '../../models/types';

/**
 * Базовый путь для навигации по NotificationRelatedKind — общий для клик-через уведомления
 * (notifications-tab.component.ts) и строки глобального индикатора фоновых процессов
 * (background-jobs-dropdown.component.ts): оба ведут по одному и тому же смыслу поля
 * (RelatedEntityId/RecordId — что это и куда оно указывает), различается только сам источник id.
 */
export function relatedKindBasePath(kind: NotificationRelatedKind): string {
  switch (kind) {
    case NotificationRelatedKind.MedicalRecordAnalysis:
      return '/health/records';
    case NotificationRelatedKind.MedicalRecordVisit:
      return '/health/visits';
    case NotificationRelatedKind.Medkit:
      return '/health/medications';
    case NotificationRelatedKind.MedicationDose:
      return '/health/intake/dose';
    case NotificationRelatedKind.MedicationCourse:
      return '/health/intake/courses';
  }
}
