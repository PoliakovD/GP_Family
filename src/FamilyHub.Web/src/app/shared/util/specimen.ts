import { SpecimenType, UserSpecimen } from '../../models/types';

/** Человекочитаемые подписи биоматериала (v2) — общие для medical-records-panel (таблица/правка
 * показателей) и indicators-tab («мои показатели», группировка по (analyteKey, specimen)). */
export const SPECIMEN_OPTIONS: { value: number; label: string }[] = [
  { value: SpecimenType.Unknown, label: 'Не указано' },
  { value: SpecimenType.Blood, label: 'Кровь' },
  { value: SpecimenType.Urine, label: 'Моча' },
  { value: SpecimenType.Stool, label: 'Кал' },
  { value: SpecimenType.VaginalSwab, label: 'Вагинальный мазок' },
  { value: SpecimenType.Saliva, label: 'Слюна' },
  { value: SpecimenType.Other, label: 'Другое' },
];

export function specimenLabel(specimen: number): string {
  return SPECIMEN_OPTIONS.find((o) => o.value === specimen)?.label ?? 'Не указано';
}

/** Подпись показателя с учётом кастомного биоматериала (UX-редизайн) — при
 * specimen === Other и известном customId показывает название из личного справочника
 * пользователя вместо общего «Другое». */
export function specimenLabelWithCustom(
  specimen: number,
  specimenCustomId: string | null | undefined,
  customs: UserSpecimen[],
): string {
  if (specimen === SpecimenType.Other && specimenCustomId) {
    const custom = customs.find((c) => c.id === specimenCustomId);
    if (custom) return custom.displayName;
  }
  return specimenLabel(specimen);
}
