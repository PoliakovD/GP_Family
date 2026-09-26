import {
  DoseOutcome, DoseSchedule, DoseScheduleMode, DoseTime, DoseUnit, FoodRelation, IntakeSubject,
} from '../../models/types';
import { MONTHS_GEN, parseLocalBirthDate } from './birthday-date';
import { pluralizeRu } from './pluralize';

/** Подписи и расчёты «Приёма лекарств» (макет «Screen - Medication schedule»): время, дозы, расписание
 * фразой и итог приёма. Общие для «Сегодня», списка курсов, карточки и формы. */

const WEEKDAY_SHORT = ['вс', 'пн', 'вт', 'ср', 'чт', 'пт', 'сб'];
const WEEKDAY_LONG = ['Воскресенье', 'Понедельник', 'Вторник', 'Среда', 'Четверг', 'Пятница', 'Суббота'];

/** Порядок дней недели в интерфейсе: с понедельника (значения — DayOfWeek: 0 — воскресенье). */
export const WEEKDAY_ORDER: readonly number[] = [1, 2, 3, 4, 5, 6, 0];

export function weekdayShort(day: number): string {
  return WEEKDAY_SHORT[day] ?? '';
}

/** «Пятница, 26 сентября» для «yyyy-MM-dd». */
export function headingDate(dateStr: string): string {
  const d = parseLocalBirthDate(dateStr);
  return `${WEEKDAY_LONG[d.getDay()]}, ${d.getDate()} ${MONTHS_GEN[d.getMonth()]}`;
}

/** «пт, 26 сен» — короткая подпись под счётчиком на мобильном. */
export function shortDate(dateStr: string): string {
  const d = parseLocalBirthDate(dateStr);
  return `${WEEKDAY_SHORT[d.getDay()]}, ${d.getDate()} ${MONTHS_GEN[d.getMonth()].slice(0, 3)}`;
}

/** Локальная сегодняшняя дата браузера «yyyy-MM-dd». */
export function todayLocal(): string {
  return toDateInput(new Date());
}

export function toDateInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function addDays(dateStr: string, days: number): string {
  const d = parseLocalBirthDate(dateStr);
  d.setDate(d.getDate() + days);
  return toDateInput(d);
}

// ── Время ────────────────────────────────────────────────────────────

/** «08:00:00» → «8:00» (как в макете: без нуля у часа). */
export function clock(t: string): string {
  const [h, m] = t.split(':');
  return `${Number(h)}:${m}`;
}

/** «08:00:00» → «08:00» для <input type="time">. */
export function toTimeInput(t: string | null | undefined): string {
  return t ? t.slice(0, 5) : '';
}

/** «08:00» → «08:00:00» для API (TimeOnly). */
export function fromTimeInput(v: string): string {
  return v.length === 5 ? `${v}:00` : v;
}

/** ISO-момент → «H:mm» по локальному времени браузера (для «принято в 8:12»). */
export function clockOfIso(iso: string): string {
  const d = new Date(iso);
  return `${d.getHours()}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/** «Прошло 5 часов» по разнице с текущим моментом. */
export function agoText(iso: string, now = Date.now()): string {
  const minutes = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60000));
  if (minutes < 60) return `${minutes} ${pluralizeRu(minutes, 'минуту', 'минуты', 'минут')}`;
  const hours = Math.round(minutes / 60);
  return `${hours} ${pluralizeRu(hours, 'час', 'часа', 'часов')}`;
}

// ── Дозы и единицы ───────────────────────────────────────────────────

const UNIT_SHORT: Record<DoseUnit, string> = {
  [DoseUnit.Tablet]: 'таб.',
  [DoseUnit.Capsule]: 'капс.',
  [DoseUnit.Ml]: 'мл',
  [DoseUnit.Drop]: 'капл.',
  [DoseUnit.Sachet]: 'пак.',
  [DoseUnit.Dose]: 'доз.',
};

const UNIT_LONG: Record<DoseUnit, string> = {
  [DoseUnit.Tablet]: 'таблетка',
  [DoseUnit.Capsule]: 'капсула',
  [DoseUnit.Ml]: 'миллилитр',
  [DoseUnit.Drop]: 'капля',
  [DoseUnit.Sachet]: 'пакетик',
  [DoseUnit.Dose]: 'доза',
};

export const DOSE_UNITS: { value: DoseUnit; label: string }[] =
  (Object.values(DoseUnit) as DoseUnit[]).map((value) => ({ value, label: UNIT_LONG[value] }));

export function unitShort(unit: DoseUnit): string {
  return UNIT_SHORT[unit] ?? '';
}

export function formatNumber(n: number): string {
  return String(Math.round(n * 100) / 100).replace('.', ',');
}

/** «1 таб.», «0,5 таб.», «2,5 мл». */
export function formatUnits(units: number, unit: DoseUnit): string {
  return `${formatNumber(units)} ${unitShort(unit)}`;
}

export const FOOD_OPTIONS: { value: FoodRelation; label: string }[] = [
  { value: FoodRelation.Before, label: 'До' },
  { value: FoodRelation.With, label: 'Во время' },
  { value: FoodRelation.After, label: 'После' },
  { value: FoodRelation.Any, label: 'Неважно' },
];

/** «до еды» / «во время еды» / «после еды»; null — не важно. */
export function foodText(food: FoodRelation): string | null {
  switch (food) {
    case FoodRelation.Before: return 'до еды';
    case FoodRelation.With: return 'во время еды';
    case FoodRelation.After: return 'после еды';
    default: return null;
  }
}

// ── Расписание фразой ────────────────────────────────────────────────

function joinAnd(items: string[]): string {
  if (items.length <= 1) return items.join('');
  return `${items.slice(0, -1).join(', ')} и ${items[items.length - 1]}`;
}

/** Времена приёма за активный день; «каждые N часов» разворачивается от времени старта. */
export function scheduleTimes(s: DoseSchedule): DoseTime[] {
  if (s.mode === DoseScheduleMode.AsNeeded) return [];
  if (s.mode === DoseScheduleMode.EveryNHours) {
    const hours = s.intervalHours ?? 0;
    if (!s.intervalStart || hours <= 0) return [];
    const [h, m] = s.intervalStart.split(':').map(Number);
    const start = h * 60 + m;
    const result: DoseTime[] = [];
    for (let minutes = start; minutes < start + 24 * 60; minutes += hours * 60) {
      const t = minutes % (24 * 60);
      result.push({ at: `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}:00`, units: s.intervalUnits ?? 1 });
    }
    return result.sort((a, b) => a.at.localeCompare(b.at));
  }
  return [...(s.times ?? [])].sort((a, b) => a.at.localeCompare(b.at));
}

/** «8:00 и 20:00». */
export function timesText(s: DoseSchedule): string {
  return joinAnd(scheduleTimes(s).map((t) => clock(t.at)));
}

function timesPerDayText(n: number): string {
  return `${n} ${pluralizeRu(n, 'раз', 'раза', 'раз')} в день`;
}

/** Суммарная доза за день не меняется по дням — берём из первой точки (для подписи «1 таб.»). */
export function firstUnits(s: DoseSchedule): number {
  if (s.mode === DoseScheduleMode.AsNeeded) return s.intervalUnits ?? 1;
  return scheduleTimes(s)[0]?.units ?? 1;
}

/** Расписание фразой без еды и срока: «2 раза в день, в 8:00 и 20:00». */
export function describePattern(s: DoseSchedule): string {
  const times = timesText(s);
  switch (s.mode) {
    case DoseScheduleMode.TimesPerDay:
      return `${timesPerDayText(s.times?.length ?? 0)}${times ? `, в ${times}` : ''}`;
    case DoseScheduleMode.EveryNHours:
      return `каждые ${s.intervalHours ?? '?'} ${pluralizeRu(s.intervalHours ?? 0, 'час', 'часа', 'часов')}${s.intervalStart ? `, начиная с ${clock(s.intervalStart)}` : ''}`;
    case DoseScheduleMode.Weekdays: {
      const days = WEEKDAY_ORDER.filter((d) => s.weekdays?.includes(d)).map(weekdayShort);
      return `${days.join(', ')}${times ? ` в ${times}` : ''}`;
    }
    case DoseScheduleMode.Cycle: {
      const on = s.cycleOnDays ?? 0;
      const off = s.cycleOffDays ?? 0;
      const head = on === 1 && off === 1
        ? 'через день'
        : `${on} ${pluralizeRu(on, 'день', 'дня', 'дней')} приём, ${off} ${pluralizeRu(off, 'день', 'дня', 'дней')} перерыв`;
      return `${head}${times ? `, в ${times}` : ''}`;
    }
    default:
      return `по необходимости, не чаще ${s.maxPerDay ?? 1} ${pluralizeRu(s.maxPerDay ?? 1, 'раза', 'раз', 'раз')} в сутки`;
  }
}

/** Итог формы: «2 раза в день, в 8:00 и 20:00, до еды · до 7 октября». */
export function describeSchedule(s: DoseSchedule, food: FoodRelation, endDate: string | null): string {
  const parts = [describePattern(s), foodText(food)].filter((p): p is string => !!p);
  const head = parts.join(', ');
  return endDate ? `${head} · до ${dayMonth(endDate)}` : head;
}

/** Короткая подпись в списках: «2 раза в день · до еды», «по необходимости · не чаще 3 раз в день». */
export function scheduleShort(s: DoseSchedule, food: FoodRelation): string {
  let head: string;
  switch (s.mode) {
    case DoseScheduleMode.TimesPerDay:
      head = timesPerDayText(s.times?.length ?? 0);
      break;
    case DoseScheduleMode.EveryNHours:
      head = `каждые ${s.intervalHours} ${pluralizeRu(s.intervalHours ?? 0, 'час', 'часа', 'часов')}`;
      break;
    case DoseScheduleMode.AsNeeded:
      head = `по необходимости · не чаще ${s.maxPerDay ?? 1} ${pluralizeRu(s.maxPerDay ?? 1, 'раза', 'раз', 'раз')} в день`;
      break;
    default:
      head = describePattern(s);
  }
  const food_ = foodText(food);
  return food_ ? `${head} · ${food_}` : head;
}

function dayMonth(dateStr: string): string {
  const d = parseLocalBirthDate(dateStr);
  return `${d.getDate()} ${MONTHS_GEN[d.getMonth()]}`;
}

export function formatDayMonthShort(dateStr: string): string {
  return dayMonth(dateStr);
}

/** «день 4 из 7» / «неделя 2 из 8» / «постоянно». Длинные курсы считаются неделями. */
export function progressText(dayNumber: number, totalDays: number | null): string {
  if (totalDays === null) return 'постоянно';
  if (dayNumber <= 0) return 'ещё не начался';
  if (totalDays > 14) return `неделя ${Math.min(Math.ceil(dayNumber / 7), Math.ceil(totalDays / 7))} из ${Math.ceil(totalDays / 7)}`;
  return `день ${Math.min(dayNumber, totalDays)} из ${totalDays}`;
}

/** Доля пройденного курса 0..1 для полосы прогресса. */
export function progressFraction(dayNumber: number, totalDays: number | null): number {
  if (!totalDays) return 0;
  return Math.max(0, Math.min(1, dayNumber / totalDays));
}

export function daysText(n: number): string {
  return `${n} ${pluralizeRu(n, 'день', 'дня', 'дней')}`;
}

/** Длительность в днях → конец курса включительно от начала (для формы «8 недель»). */
export function endDateFor(start: string, days: number): string {
  return addDays(start, Math.max(0, days - 1));
}

export function daysBetween(startDate: string, endDate: string): number {
  const ms = parseLocalBirthDate(endDate).getTime() - parseLocalBirthDate(startDate).getTime();
  return Math.round(ms / 86_400_000) + 1;
}

// ── Люди и итоги ─────────────────────────────────────────────────────

/** Ключ фильтра «Сегодня» для человека: me | u:{id} | d:{id}. */
export function subjectKey(s: IntakeSubject): string {
  if (s.isSelf) return 'me';
  return s.kind === 'dependent' ? `d:${s.id}` : `u:${s.id}`;
}

export function subjectName(s: IntakeSubject): string {
  return s.isSelf ? 'Я' : s.name;
}

/** Класс состояния приёма для цветов сетки/строк: ok / late / missed / skipped / upcoming / due. */
export function outcomeClass(o: DoseOutcome): string {
  switch (o) {
    case DoseOutcome.OnTime: return 'ok';
    case DoseOutcome.Late: return 'late';
    case DoseOutcome.Missed: return 'missed';
    case DoseOutcome.Skipped: return 'skipped';
    case DoseOutcome.Due: return 'due';
    default: return 'upcoming';
  }
}

export function outcomeLabel(o: DoseOutcome): string {
  switch (o) {
    case DoseOutcome.OnTime: return 'вовремя';
    case DoseOutcome.Late: return 'с опозданием';
    case DoseOutcome.Missed: return 'пропущен';
    case DoseOutcome.Skipped: return 'пропущен осознанно';
    case DoseOutcome.Due: return 'пора принять';
    default: return 'впереди';
  }
}

/** «хватит на 3 дня» из остатка. */
export function coversText(daysCovered: number): string {
  return `хватит на ${daysText(daysCovered)}`;
}
