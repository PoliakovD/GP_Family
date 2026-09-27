// Точный возраст в годах+месяцах — нужен для «Прививок» (шапка человека: «6 лет 10 мес»),
// нигде в проекте раньше не считался (день рождения оперирует только «сколько исполнится»,
// см. birthday-date.ts).

import { parseLocalBirthDate } from './birthday-date';
import { pluralizeRu } from './pluralize';

export interface AgeParts {
  years: number;
  months: number;
}

/** "yyyy-MM-dd" -> {years, months} на дату `on` (по умолчанию — сегодня). Отрицательный возраст
 * (дата рождения в будущем — некорректные данные) отдаёт {0, 0}. */
export function ageParts(birthDateStr: string, on: Date = new Date()): AgeParts {
  const birth = parseLocalBirthDate(birthDateStr);
  let years = on.getFullYear() - birth.getFullYear();
  let months = on.getMonth() - birth.getMonth();
  if (on.getDate() < birth.getDate()) months--;
  if (months < 0) {
    years--;
    months += 12;
  }
  return years < 0 ? { years: 0, months: 0 } : { years, months };
}

/** «6 лет 10 мес», «8 мес», «меньше месяца». */
export function ageText(birthDateStr: string, on: Date = new Date()): string {
  const { years, months } = ageParts(birthDateStr, on);
  if (years === 0 && months === 0) return 'меньше месяца';
  const parts: string[] = [];
  if (years > 0) parts.push(`${years} ${pluralizeRu(years, 'год', 'года', 'лет')}`);
  if (months > 0) parts.push(`${months} ${pluralizeRu(months, 'месяц', 'месяца', 'месяцев')}`);
  return parts.join(' ');
}

/** Короткая форма для карточек/списков: «6 лет», «8 мес» — без второй единицы. */
export function ageShortText({ years, months }: AgeParts): string {
  if (years > 0) return `${years} ${pluralizeRu(years, 'год', 'года', 'лет')}`;
  if (months > 0) return `${months} ${pluralizeRu(months, 'месяц', 'месяца', 'месяцев')}`;
  return 'меньше месяца';
}
