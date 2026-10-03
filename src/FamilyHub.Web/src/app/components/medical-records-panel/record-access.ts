// Видимость медзаписи семьям — двухуровневая модель: L1 — общий шаринг «Анализы + Врачи» семье
// (shares), L2 — скрытие конкретной записи от семьи (record.hiddenFamilyIds). Чистые функции.

import type { MedicalRecord } from '../../models/types';

/** Видна ли КОНКРЕТНАЯ запись данной семье: (L1 share есть) И (L2 hide нет). */
export function isVisibleToFamily(record: MedicalRecord, shares: readonly string[], familyId: string): boolean {
  return shares.includes(familyId) && !record.hiddenFamilyIds.includes(familyId);
}

/** «Только я» — ни одной семье запись не видна (или шаринга нет вовсе). */
export function isOnlyMe(record: MedicalRecord, shares: readonly string[]): boolean {
  return shares.length === 0 || !shares.some((fid) => !record.hiddenFamilyIds.includes(fid));
}

/** Сводка для карточки: «Только вы» / «Все семьи» / «Все семьи, кроме N». */
export function accessSummary(record: MedicalRecord, shares: readonly string[]): string {
  const total = shares.length;
  if (total === 0) return 'Только вы';
  const hiddenCount = shares.filter((fid) => record.hiddenFamilyIds.includes(fid)).length;
  if (hiddenCount === total) return 'Только вы';
  if (hiddenCount === 0) return 'Все семьи';
  return `Все семьи, кроме ${hiddenCount}`;
}
