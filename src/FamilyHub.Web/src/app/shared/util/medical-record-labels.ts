// Вынесено из medical-records-panel.component.ts (редизайн v3, PR7) — переиспользуется и там
// (список/фильтры), и в новом record-add.component.ts (форма создания на отдельном роуте).

import { MedicalRecordKind } from '../../models/types';

export interface MedicalRecordKindLabels {
  /** Заголовок экрана (редизайн v2.1) — раньше печатался самой Page-обёрткой
   * (medical-records-tab/doctor-visits-tab), переехал сюда вместе со сводкой под ним. */
  title: string;
  /** Существительное для счётчика записей в сводке под заголовком и в шапке группы-человека
   * ("N анализов"/"N посещений") — три формы, как у pluralizeRu. */
  countNounOne: string;
  countNounFew: string;
  countNounMany: string;
  /** Вторая строка сводки под заголовком — поясняет умолчание доступа (см. Screen - Tests.dc.html). */
  accessHintLabel: string;
  addButtonLabel: string;
  doctorPlaceholder: string;
  descriptionPlaceholder: string;
  searchPlaceholder: string;
  emptyLabel: string;
}

/** Подписи различаются по виду записи — тот же идиом, что TYPE_LABEL/TYPE_ICON в home.component.ts. */
export const MEDICAL_RECORD_KIND_LABELS: Record<MedicalRecordKind, MedicalRecordKindLabels> = {
  [MedicalRecordKind.Analysis]: {
    title: 'Анализы',
    countNounOne: 'анализ',
    countNounFew: 'анализа',
    countNounMany: 'анализов',
    accessHintLabel: 'Ваши анализы видите только вы, пока сами не откроете доступ.',
    addButtonLabel: 'Добавить запись',
    doctorPlaceholder: 'Врач (необязательно)',
    descriptionPlaceholder: 'Описание (необязательно)',
    searchPlaceholder: 'Поиск по анализам…',
    emptyLabel: 'Записей нет.',
  },
  [MedicalRecordKind.DoctorVisit]: {
    title: 'Посещения врачей',
    countNounOne: 'посещение',
    countNounFew: 'посещения',
    countNounMany: 'посещений',
    accessHintLabel: 'Ваши посещения видят только вы, пока сами не откроете доступ.',
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
