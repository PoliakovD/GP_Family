import {
  Bell, BellRing, BookOpenText, Cake, CircleAlert, FileText, LogOut, MessageCircle, NotebookPen, Package, Pill,
  Syringe, UserCheck, WandSparkles,
} from 'lucide';
import type { IconNode as LucideIconData } from 'lucide';
import { NotificationType } from '../../models/types';
import type { Tone } from '../ui/tone';

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
  // Терминальный отказ (не техническая недоступность LM Studio — та молча резюмируется, см.
  // LmStudioRecoverySweepJob, и уведомления не создаёт).
  [NotificationType.MedicalDocumentExtractionFailed]: 'Не удалось распознать документ',
  [NotificationType.MedicationEnrichmentFailed]: 'Не удалось найти информацию о препарате',
  [NotificationType.MedicationDoseDue]: 'Пора принять лекарство',
  [NotificationType.MedicationDoseMissed]: 'Пропущен приём лекарства',
  [NotificationType.MedicationStockLow]: 'Лекарство заканчивается',
  [NotificationType.VaccinationDue]: 'Скоро прививка',
  [NotificationType.VaccinationOverdue]: 'Можно сделать прививку',
  [NotificationType.VaccinationWellbeingCheck]: 'Как самочувствие после прививки',
  [NotificationType.DoctorReportAboutYou]: 'Член семьи составил отчёт для врача о вас',
  [NotificationType.FamilyChatMessage]: 'Новое сообщение в чате семьи',
};

export const NOTIFICATION_TYPE_ICON: Record<number, LucideIconData> = {
  [NotificationType.MedicationExpiringSoon]: CircleAlert,
  [NotificationType.MedicationExpired]: CircleAlert,
  [NotificationType.BirthdayUpcoming]: Cake,
  [NotificationType.MemberLeft]: LogOut,
  [NotificationType.MemberApproved]: UserCheck,
  [NotificationType.MedicalRecordShared]: FileText,
  [NotificationType.MedicationEnriched]: BookOpenText,
  [NotificationType.MedicalDocumentExtracted]: WandSparkles,
  [NotificationType.MedicalDocumentExtractionFailed]: CircleAlert,
  [NotificationType.MedicationEnrichmentFailed]: CircleAlert,
  [NotificationType.MedicationDoseDue]: Pill,
  [NotificationType.MedicationDoseMissed]: BellRing,
  [NotificationType.MedicationStockLow]: Package,
  [NotificationType.VaccinationDue]: Syringe,
  [NotificationType.VaccinationOverdue]: Syringe,
  [NotificationType.VaccinationWellbeingCheck]: NotebookPen,
  [NotificationType.DoctorReportAboutYou]: FileText,
  [NotificationType.FamilyChatMessage]: MessageCircle,
};

/** Тон плитки по смыслу (DESIGN §1): danger — просрочено/пропущено/ошибка, warning — скоро,
 * success — готово; остальное — neutral. «Можно сделать прививку» — neutral, как в vaccination-labels. */
export const NOTIFICATION_TYPE_TONE: Record<number, Tone> = {
  [NotificationType.MedicationExpired]: 'danger',
  [NotificationType.MedicalDocumentExtractionFailed]: 'danger',
  [NotificationType.MedicationEnrichmentFailed]: 'danger',
  [NotificationType.MedicationDoseMissed]: 'danger',
  [NotificationType.MedicationExpiringSoon]: 'warning',
  [NotificationType.MedicationStockLow]: 'warning',
  [NotificationType.VaccinationDue]: 'warning',
  [NotificationType.MemberApproved]: 'success',
  [NotificationType.MedicalDocumentExtracted]: 'success',
};

export function notificationTypeLabel(type: number): string {
  return NOTIFICATION_TYPE_LABEL[type] ?? 'Оповещение';
}

export function notificationTypeIcon(type: number): LucideIconData {
  return NOTIFICATION_TYPE_ICON[type] ?? Bell;
}

export function notificationTypeTone(type: number): Tone {
  return NOTIFICATION_TYPE_TONE[type] ?? 'neutral';
}

/** Группировка по разделу (редизайн v3, PR8 — "Аптечка"/"Семья"/"Доступ к записям" вместо
 * плоского списка строк таблицы, см. мокап). */
export const NOTIFICATION_TYPE_SECTION: Record<number, string> = {
  [NotificationType.MedicationExpiringSoon]: 'Аптечка',
  [NotificationType.MedicationExpired]: 'Аптечка',
  [NotificationType.MedicationEnriched]: 'Аптечка',
  [NotificationType.MedicationEnrichmentFailed]: 'Аптечка',
  [NotificationType.MedicationDoseDue]: 'Приём лекарств',
  [NotificationType.MedicationDoseMissed]: 'Приём лекарств',
  [NotificationType.MedicationStockLow]: 'Приём лекарств',
  [NotificationType.VaccinationDue]: 'Прививки',
  [NotificationType.VaccinationOverdue]: 'Прививки',
  [NotificationType.VaccinationWellbeingCheck]: 'Прививки',
  [NotificationType.BirthdayUpcoming]: 'Семья',
  [NotificationType.MemberLeft]: 'Семья',
  [NotificationType.MemberApproved]: 'Семья',
  [NotificationType.FamilyChatMessage]: 'Семья',
  [NotificationType.MedicalRecordShared]: 'Доступ к записям',
  [NotificationType.MedicalDocumentExtracted]: 'Доступ к записям',
  [NotificationType.MedicalDocumentExtractionFailed]: 'Доступ к записям',
  [NotificationType.DoctorReportAboutYou]: 'Доступ к записям',
};

export function notificationTypeSection(type: number): string {
  return NOTIFICATION_TYPE_SECTION[type] ?? 'Прочее';
}
