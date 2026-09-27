// Подписи и цвета «Прививок» (макет «Screen - Vaccination»): статус графика, возрастной этап,
// вид отметки. Общее для обзора, графика человека, детали и модалок.

import { VaccinationKind, VaccinationStatus, VaccineGroup } from '../../models/types';
import { formatDayMonthYear, formatYear } from './date-format';
import { MONTHS_GEN, parseLocalBirthDate } from './birthday-date';

const STATUS_LABEL: Record<number, string> = {
  [VaccinationStatus.Done]: 'Сделано',
  [VaccinationStatus.HadDisease]: 'Перенесённая болезнь',
  [VaccinationStatus.DueSoon]: 'Скоро',
  [VaccinationStatus.CanDo]: 'Можно сделать',
  [VaccinationStatus.Upcoming]: 'Впереди',
  [VaccinationStatus.NoData]: 'Нет данных',
};

const STATUS_ICON: Record<number, string> = {
  [VaccinationStatus.Done]: 'ph-fill ph-check-circle',
  [VaccinationStatus.HadDisease]: 'ph-fill ph-check-circle',
  [VaccinationStatus.DueSoon]: 'ph-fill ph-clock',
  [VaccinationStatus.CanDo]: 'ph-fill ph-arrow-circle-right',
  [VaccinationStatus.Upcoming]: 'ph ph-circle-dashed',
  [VaccinationStatus.NoData]: 'ph ph-question',
};

/** Тот же цвет, что уже используется в проекте для этих смыслов (--color-status-*), кроме
 * «можно сделать» — там сознательно не статус-danger (макет: «просрочка — это не страшно»,
 * красный в приложении означает анализы вне нормы, не прививки). */
const STATUS_COLOR: Record<number, string> = {
  [VaccinationStatus.Done]: 'var(--color-status-ok)',
  [VaccinationStatus.HadDisease]: 'var(--color-status-ok)',
  [VaccinationStatus.DueSoon]: 'var(--color-accent)',
  [VaccinationStatus.CanDo]: 'var(--color-status-warning)',
  [VaccinationStatus.Upcoming]: 'var(--color-neutral-500)',
  [VaccinationStatus.NoData]: 'var(--color-neutral-500)',
};

export function statusLabel(status: number): string {
  return STATUS_LABEL[status] ?? '';
}

export function statusIcon(status: number): string {
  return STATUS_ICON[status] ?? 'ph ph-circle';
}

export function statusColor(status: number): string {
  return STATUS_COLOR[status] ?? 'var(--color-neutral-500)';
}

const STAGE_LABEL: Record<string, string> = {
  '0-1': '0 — 1 год',
  '1-2': '1 — 2 года',
  '2-6': '2 — 6 лет',
  '6-7': '6 — 7 лет',
  '14+': '14 лет',
  adult: 'Взрослые',
  epidemic: 'По эпидпоказаниям',
  closed: 'Перенесённые болезни',
  custom: 'Не из календаря',
};

export function stageLabel(stage: string): string {
  return STAGE_LABEL[stage] ?? stage;
}

/** Порядок этапов на графике человека (вид «По возрасту»). */
export const STAGE_ORDER = ['0-1', '1-2', '2-6', '6-7', '14+', 'adult', 'epidemic', 'closed', 'custom'];

export const GROUP_LABEL: Record<number, string> = {
  [VaccineGroup.National]: 'Национальный календарь',
  [VaccineGroup.Adult]: 'Для взрослых',
  [VaccineGroup.Epidemic]: 'По эпидпоказаниям',
};

/** Подпись чипа «болела»/«не помню»/«сделано» (по умолчанию) в форме отметки. */
export function kindChipLabel(kind: number): string {
  switch (kind) {
    case VaccinationKind.HadDisease: return 'болел(а)';
    case VaccinationKind.Unknown: return 'не помню';
    default: return 'сделано';
  }
}

/** Дата с учётом точности («9 июня 2026» / «июня 2026» → упрощаем до года / «2026»). */
export function preciseDateText(date: string | null, precision: number | null): string {
  if (!date) return '—';
  if (precision === 2 /* Year */) return formatYear(date);
  if (precision === 1 /* Month */) {
    const d = parseLocalBirthDate(date);
    return `${MONTHS_GEN[d.getMonth()]} ${d.getFullYear()}`;
  }
  return formatDayMonthYear(date);
}

/** «до 4 ноября» / «до ноября 2026» — срок окна для карточки «Стоит запланировать». */
export function windowToText(windowTo: string | null): string {
  return windowTo ? `до ${formatDayMonthYear(windowTo)}` : '';
}
