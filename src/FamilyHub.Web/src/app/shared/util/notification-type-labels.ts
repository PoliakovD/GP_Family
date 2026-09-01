import { NotificationType } from '../../models/types';

/**
 * Подписи и иконки по FamilyHub.Domain.Enums.NotificationType. Общее для ленты оповещений
 * (notifications-tab) и вкладки «Настройки → Уведомления» — раньше жило только внутри
 * notifications-tab.component.ts и покрывало 3 из 6 значений enum'а.
 */
export const NOTIFICATION_TYPE_LABEL: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'Срок годности скоро истекает',
  [NotificationType.MedicationExpired]: 'Срок годности истёк',
  [NotificationType.BirthdayUpcoming]: 'Скоро день рождения',
  [NotificationType.MemberLeft]: 'Участник покинул семью',
  [NotificationType.MemberApproved]: 'Заявка на вступление одобрена',
  [NotificationType.MedicalRecordShared]: 'Открыт доступ к медицинской записи',
  // Редизайн v3, PR8 — раньше не имели подписи (падали в дефолт "Оповещение", непереведённый
  // ключ, см. Screen - Profile settings.dc.html).
  [NotificationType.MedicationEnriched]: 'Справочник по лекарству обновлён',
  [NotificationType.MedicalDocumentExtracted]: 'Медицинский документ распознан',
};

export const NOTIFICATION_TYPE_ICON: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'ph-duotone ph-warning-circle',
  [NotificationType.MedicationExpired]: 'ph-duotone ph-warning-circle',
  [NotificationType.BirthdayUpcoming]: 'ph-duotone ph-cake',
  [NotificationType.MemberLeft]: 'ph-duotone ph-sign-out',
  [NotificationType.MemberApproved]: 'ph-duotone ph-user-check',
  [NotificationType.MedicalRecordShared]: 'ph-duotone ph-file-text',
  [NotificationType.MedicationEnriched]: 'ph-duotone ph-book-open-text',
  [NotificationType.MedicalDocumentExtracted]: 'ph-duotone ph-magic-wand',
};

export function notificationTypeLabel(type: number): string {
  return NOTIFICATION_TYPE_LABEL[type] ?? 'Оповещение';
}

export function notificationTypeIcon(type: number): string {
  return NOTIFICATION_TYPE_ICON[type] ?? 'ph-duotone ph-bell';
}

/** Группировка по разделу (редизайн v3, PR8 — "Аптечка"/"Семья"/"Доступ к записям" вместо
 * плоского списка строк таблицы, см. мокап). */
export const NOTIFICATION_TYPE_SECTION: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'Аптечка',
  [NotificationType.MedicationExpired]: 'Аптечка',
  [NotificationType.MedicationEnriched]: 'Аптечка',
  [NotificationType.BirthdayUpcoming]: 'Семья',
  [NotificationType.MemberLeft]: 'Семья',
  [NotificationType.MemberApproved]: 'Семья',
  [NotificationType.MedicalRecordShared]: 'Доступ к записям',
  [NotificationType.MedicalDocumentExtracted]: 'Доступ к записям',
};

export function notificationTypeSection(type: number): string {
  return NOTIFICATION_TYPE_SECTION[type] ?? 'Прочее';
}
