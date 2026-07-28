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
};

export const NOTIFICATION_TYPE_ICON: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'ph-duotone ph-warning-circle',
  [NotificationType.MedicationExpired]: 'ph-duotone ph-warning-circle',
  [NotificationType.BirthdayUpcoming]: 'ph-duotone ph-cake',
  [NotificationType.MemberLeft]: 'ph-duotone ph-sign-out',
  [NotificationType.MemberApproved]: 'ph-duotone ph-user-check',
  [NotificationType.MedicalRecordShared]: 'ph-duotone ph-file-text',
};

export function notificationTypeLabel(type: number): string {
  return NOTIFICATION_TYPE_LABEL[type] ?? 'Оповещение';
}

export function notificationTypeIcon(type: number): string {
  return NOTIFICATION_TYPE_ICON[type] ?? 'ph-duotone ph-bell';
}
