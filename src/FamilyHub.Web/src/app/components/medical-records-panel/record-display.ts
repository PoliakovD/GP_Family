// Подписи медзаписи для списка и экрана записи — чистые функции (вынесено из
// MedicalRecordsPanelComponent).

import { MedicalRecordKind } from '../../models/types';
import type { MedicalRecord } from '../../models/types';
import { pluralizeRu } from '../../shared/util/pluralize';

/** Название записи: распознанное/введённое или вид записи. */
export function recordShortName(item: MedicalRecord): string {
  return item.title ?? (item.kind === MedicalRecordKind.Analysis ? 'Анализ' : 'Приём врача');
}

/** Ключ человека — тот же, что у фильтра «Пациент» (patient-options). */
export function recordPersonKey(item: MedicalRecord): string {
  if (item.familyDependentId) return `dep:${item.familyDependentId}`;
  return `user:${item.targetUserId ?? item.ownerUserId}`;
}

/** «скан» переименовано в «файл»: вложение не обязательно скан (PDF, фото с телефона). */
export function attachmentCountLabel(item: MedicalRecord): string {
  return `${item.attachmentCount} ${pluralizeRu(item.attachmentCount, 'файл', 'файла', 'файлов')}`;
}

export function indicatorCountLabel(item: MedicalRecord): string {
  return `${item.indicatorCount} ${pluralizeRu(item.indicatorCount, 'показатель', 'показателя', 'показателей')}`;
}

/** «Без нормы в бланке» — остаток от счётчиков «вне нормы»/«в норме», которые приходят с сервера. */
export function unknownIndicatorCount(item: MedicalRecord): number {
  return Math.max(0, item.indicatorCount - item.abnormalIndicatorCount - item.normalIndicatorCount);
}
