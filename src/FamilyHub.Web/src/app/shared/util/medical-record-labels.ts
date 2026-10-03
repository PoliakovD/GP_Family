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
  /** Пустой раздел у нового пользователя — что здесь будет и как начать (раньше — одна серая строка). */
  emptyHint: string;
}

/** Подписи различаются по виду записи — тот же идиом, что TYPE_LABEL/TYPE_ICON в home.component.ts. */
export const MEDICAL_RECORD_KIND_LABELS: Record<MedicalRecordKind, MedicalRecordKindLabels> = {
  [MedicalRecordKind.Analysis]: {
    title: 'Анализы',
    countNounOne: 'анализ',
    countNounFew: 'анализа',
    countNounMany: 'анализов',
    accessHintLabel: 'Ваши анализы видите только вы, пока сами не откроете доступ.',
    addButtonLabel: 'Добавить анализ',
    doctorPlaceholder: 'Врач (необязательно)',
    descriptionPlaceholder: 'Описание (необязательно)',
    searchPlaceholder: 'Поиск по анализам…',
    emptyLabel: 'Анализов пока нет',
    emptyHint: 'Сфотографируйте бланк из лаборатории или загрузите PDF — показатели и нормы распознаются автоматически, а вы потом их проверите.',
  },
  [MedicalRecordKind.DoctorVisit]: {
    // Везде «приём врача» — раньше один и тот же объект назывался приёмом, посещением и визитом.
    title: 'Приёмы врача',
    countNounOne: 'приём',
    countNounFew: 'приёма',
    countNounMany: 'приёмов',
    accessHintLabel: 'Ваши приёмы врача видите только вы, пока сами не откроете доступ.',
    addButtonLabel: 'Добавить приём',
    doctorPlaceholder: 'Врач / специальность',
    descriptionPlaceholder: 'Заключение (необязательно)',
    searchPlaceholder: 'Поиск по приёмам врача…',
    emptyLabel: 'Приёмов врача пока нет',
    emptyHint: 'Сфотографируйте заключение врача — назначения распознаются, и по ним можно будет одним нажатием начать курс приёма лекарств.',
  },
};

/** Базовый роут вида записи — используется и «Добавить»-навигацией, и мобильной навигацией на
 * экран открытой записи. */
export function medicalRecordKindBasePath(kind: MedicalRecordKind): string {
  return kind === MedicalRecordKind.Analysis ? '/health/records' : '/health/visits';
}
