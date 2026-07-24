// Общая датовая арифметика дней рождения — вынесена из BirthdaysPanelComponent (была private),
// чтобы не дублировать её в BirthdayWidgetComponent (виджет на Главной, редизайн навигации).
// Оперирует строкой "yyyy-MM-dd" (DateOnly с бэкенда), а не готовым Birthday — переиспользуется
// и там, где под рукой только дата без остальных полей.

export const MONTHS_NOM = [
  'Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь',
  'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь',
];

export const MONTHS_GEN = [
  'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
  'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря',
];

/** "yyyy-MM-dd" -> Date в локальной полуночи (без сдвига часовым поясом, в отличие от `new Date(str)`). */
export function parseLocalBirthDate(dateStr: string): Date {
  const [y, m, d] = dateStr.split('-').map(Number);
  return new Date(y, m - 1, d);
}

/** Сколько дней до ближайшего наступления дня рождения (0 — сегодня, считая от локальной полуночи). */
export function daysUntilNextBirthday(dateStr: string): number {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const bday = parseLocalBirthDate(dateStr);
  let next = new Date(today.getFullYear(), bday.getMonth(), bday.getDate());
  if (next.getTime() < today.getTime()) {
    next = new Date(today.getFullYear() + 1, bday.getMonth(), bday.getDate());
  }
  return Math.round((next.getTime() - today.getTime()) / 86_400_000);
}

/** Возраст, который исполнится в ближайшее наступление дня рождения. */
export function nextBirthdayAge(dateStr: string): number {
  const bday = parseLocalBirthDate(dateStr);
  const daysUntil = daysUntilNextBirthday(dateStr);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const nextOccurrence = new Date(today.getTime() + daysUntil * 86_400_000);
  return nextOccurrence.getFullYear() - bday.getFullYear();
}

/** Имя + акцент на срочности для ближайших дней рождения (как в дизайн-дэке). */
export function birthdayUrgencyLabel(personName: string, dateStr: string): string {
  const days = daysUntilNextBirthday(dateStr);
  if (days === 0) return `${personName} — сегодня!`;
  if (days === 1) return `${personName} — уже завтра!`;
  return personName;
}

export function birthdayMetaLabel(dateStr: string): string {
  const bday = parseLocalBirthDate(dateStr);
  return `${bday.getDate()} ${MONTHS_GEN[bday.getMonth()]} · исполняется ${nextBirthdayAge(dateStr)}`;
}
