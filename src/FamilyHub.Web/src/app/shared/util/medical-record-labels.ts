// Вынесено из medical-records-panel.component.ts (редизайн v3, PR7) — переиспользуется и там
// (список/фильтры), и в новом record-add.component.ts (форма создания на отдельном роуте).

import { MedicalRecordKind } from '../../models/types';

export interface MedicalRecordKindLabels {
  addButtonLabel: string;
  doctorPlaceholder: string;
  descriptionPlaceholder: string;
  searchPlaceholder: string;
  emptyLabel: string;
}

/** Подписи различаются по виду записи — тот же идиом, что TYPE_LABEL/TYPE_ICON в home.component.ts. */
export const MEDICAL_RECORD_KIND_LABELS: Record<MedicalRecordKind, MedicalRecordKindLabels> = {
  [MedicalRecordKind.Analysis]: {
    addButtonLabel: 'Добавить запись',
    doctorPlaceholder: 'Врач (необязательно)',
    descriptionPlaceholder: 'Описание (необязательно)',
    searchPlaceholder: 'Поиск по анализам…',
    emptyLabel: 'Записей нет.',
  },
  [MedicalRecordKind.DoctorVisit]: {
    addButtonLabel: 'Добавить посещение',
    doctorPlaceholder: 'Врач / специальность',
    descriptionPlaceholder: 'Заключение (необязательно)',
    searchPlaceholder: 'Поиск по посещениям…',
    emptyLabel: 'Посещений нет.',
  },
};

/** Базовый роут вида записи — используется и «Добавить»-навигацией, и мобильной навигацией на
 * экран открытой записи. */
export function medicalRecordKindBasePath(kind: MedicalRecordKind): string {
  return kind === MedicalRecordKind.Analysis ? '/health/records' : '/health/visits';
}
