// Редизайн v3 — человеческий формат дат вместо сырого "гггг-мм-дд" на "Анализах" (таймлайн,
// заголовок группы). Переиспользует таблицу месяцев и парсер даты из birthday-date.ts — не
// заводит вторую копию MONTHS_GEN ради другого экрана.

import { MONTHS_GEN, parseLocalBirthDate } from './birthday-date';

/** "yyyy-MM-dd" -> "9 июня". */
export function formatDayMonth(dateStr: string): string {
  const d = parseLocalBirthDate(dateStr);
  return `${d.getDate()} ${MONTHS_GEN[d.getMonth()]}`;
}

/** "yyyy-MM-dd" -> "9 июня 2026". */
export function formatDayMonthYear(dateStr: string): string {
  const d = parseLocalBirthDate(dateStr);
  return `${d.getDate()} ${MONTHS_GEN[d.getMonth()]} ${d.getFullYear()}`;
}

/** "yyyy-MM-dd" -> "2026" — вторая, приглушённая строка двухстрочного даты-бейджа в таймлайне. */
export function formatYear(dateStr: string): string {
  return String(parseLocalBirthDate(dateStr).getFullYear());
}
