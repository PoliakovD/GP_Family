// Подписи, метаданные и форматирование дневника самочувствия. Ключи локализации/факторов и
// коды замеров — контракт с бэкендом (FamilyHub.Domain.HealthNotes), человекочитаемые подписи —
// только здесь. Каталог замеров (названия/единицы) приходит с API — здесь его копии нет.

import {
  HealthMetricDefinition, HealthNote, HealthNoteKind, SleepData,
} from '../../models/types';
import { MONTHS_GEN } from './birthday-date';

export interface HealthKindMeta {
  kind: HealthNoteKind;
  /** Ед. число — для формы («Симптом»). */
  label: string;
  /** Мн. число — для фильтра ленты («Симптомы»). */
  filterLabel: string;
  /** Имя иконки Phosphor без префикса `ph-`. */
  icon: string;
  /** CSS-цвет типа (токен --color-kind-*). */
  color: string;
}

/** Порядок — как в макете: и для выбора типа в форме, и для фильтра ленты. */
export const HEALTH_KINDS: HealthKindMeta[] = [
  { kind: HealthNoteKind.Symptom, label: 'Симптом', filterLabel: 'Симптомы', icon: 'lightning', color: 'var(--color-kind-symptom)' },
  { kind: HealthNoteKind.Metric, label: 'Замер', filterLabel: 'Замеры', icon: 'heartbeat', color: 'var(--color-kind-metric)' },
  { kind: HealthNoteKind.Wellbeing, label: 'Самочувствие', filterLabel: 'Самочувствие', icon: 'smiley', color: 'var(--color-kind-wellbeing)' },
  { kind: HealthNoteKind.MedicationIntake, label: 'Лекарство', filterLabel: 'Лекарства', icon: 'pill', color: 'var(--color-kind-medication)' },
  { kind: HealthNoteKind.Sleep, label: 'Сон', filterLabel: 'Сон', icon: 'moon-stars', color: 'var(--color-kind-sleep)' },
  { kind: HealthNoteKind.Note, label: 'Заметка', filterLabel: 'Заметки', icon: 'note-pencil', color: 'var(--color-kind-note)' },
];

export function kindMeta(kind: HealthNoteKind): HealthKindMeta {
  return HEALTH_KINDS.find((k) => k.kind === kind) ?? HEALTH_KINDS[0];
}

export const BODY_AREA_LABELS: Record<string, string | undefined> = {
  head: 'Голова',
  chest: 'Грудь',
  abdomen: 'Живот',
  back: 'Спина',
  joints: 'Суставы',
  throat: 'Горло',
};

export const WELLBEING_FACTOR_LABELS: Record<string, string | undefined> = {
  rested: 'Выспался',
  stress: 'Стресс',
  sport: 'Спорт',
  weather: 'Погода',
};

export interface WellbeingLevel {
  score: number;
  label: string;
  /** Имя иконки Phosphor без префикса `ph-`. */
  icon: string;
}

export const WELLBEING_LEVELS: WellbeingLevel[] = [
  { score: 1, label: 'Ужасно', icon: 'smiley-x-eyes' },
  { score: 2, label: 'Плохо', icon: 'smiley-sad' },
  { score: 3, label: 'Так себе', icon: 'smiley-meh' },
  { score: 4, label: 'Хорошо', icon: 'smiley' },
  { score: 5, label: 'Отлично', icon: 'smiley-wink' },
];

export function wellbeingLevel(score: number): WellbeingLevel {
  return WELLBEING_LEVELS.find((l) => l.score === score) ?? WELLBEING_LEVELS[2];
}

export const SLEEP_QUALITY_LABELS: Record<number, string> = { 1: 'Плохо', 2: 'Средне', 3: 'Хорошо' };

/** Расшифровка интенсивности словами (рядом с выбранным числом 1–10). */
export function severityWord(severity: number): string {
  if (severity <= 2) return 'слабая';
  if (severity <= 4) return 'умеренная';
  if (severity <= 6) return 'выраженная';
  if (severity <= 8) return 'сильная';
  return severity === 10 ? 'невыносимая' : 'очень сильная';
}

/** 128 → "128", 81.4 → "81,4" (десятичная запятая). */
export function formatNumber(n: number): string {
  return String(Math.round(n * 100) / 100).replace('.', ',');
}

/** 440 → "7 ч 20 мин", 60 → "1 ч", 45 → "45 мин". */
export function formatDuration(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = Math.round(minutes % 60);
  if (h === 0) return `${m} мин`;
  return m === 0 ? `${h} ч` : `${h} ч ${m} мин`;
}

export function sleepMinutes(sleep: SleepData): number {
  return Math.round((new Date(sleep.wakeTime).getTime() - new Date(sleep.bedTime).getTime()) / 60000);
}

/** Значение замера для показа: «128/84», «72», «81,4». */
export function metricValueText(value: number, value2?: number | null): string {
  return value2 != null ? `${formatNumber(value)}/${formatNumber(value2)}` : formatNumber(value);
}

// ---- Время (локальное) ----

const WEEKDAYS = ['воскресенье', 'понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота'];

const pad = (n: number) => String(n).padStart(2, '0');

/** "2026-09-26T08:10:00Z" → "8:10" (локальное время, без ведущего нуля у часов — как в макете). */
export function formatClock(iso: string): string {
  const d = new Date(iso);
  return `${d.getHours()}:${pad(d.getMinutes())}`;
}

/** Локальная дата записи как ключ дня "yyyy-MM-dd" — по нему запись группируется в ленте. */
export function dayKey(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export interface DayHeading {
  /** «Сегодня» / «Вчера» / «25 сентября». */
  title: string;
  /** «пятница, 26 сентября» — вторая строка заголовка. */
  subtitle: string;
}

export function dayHeading(key: string, now: Date = new Date()): DayHeading {
  const [y, m, d] = key.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const diffDays = Math.round((today.getTime() - date.getTime()) / 86400000);
  const dayMonth = `${d} ${MONTHS_GEN[m - 1]}`;
  const subtitle = `${WEEKDAYS[date.getDay()]}, ${dayMonth}`;
  if (diffDays === 0) return { title: 'Сегодня', subtitle };
  if (diffDays === 1) return { title: 'Вчера', subtitle };
  return { title: dayMonth, subtitle: WEEKDAYS[date.getDay()] };
}

/** Date → значение для `<input type="datetime-local">` (локальное, без секунд). */
export function toLocalInput(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Значение `datetime-local` → ISO в UTC для API. */
export function fromLocalInput(value: string): string {
  return new Date(value).toISOString();
}

// ---- Описание записи для ленты ----

export interface NoteDescription {
  title: string;
  /** Вторая, приглушённая часть строки; пусто — не показываем. */
  subtitle: string;
  /** Бейдж справа (интенсивность симптома). */
  badge: string | null;
}

/** Собирает строку ленты по записи. `metrics` — каталог замеров; пока он не загружен, замер
 * показывается по коду (лента не блокируется загрузкой справочника). */
export function describeNote(note: HealthNote, metrics: HealthMetricDefinition[]): NoteDescription {
  const parts: string[] = [];
  const push = (s: string | null | undefined) => { if (s && s.trim()) parts.push(s.trim()); };

  switch (note.kind) {
    case HealthNoteKind.Symptom: {
      const s = note.symptom;
      push((s?.areas ?? []).map((a) => BODY_AREA_LABELS[a] ?? a).join(', ').toLowerCase());
      push(s?.detail);
      push(note.text);
      return { title: note.title ?? 'Симптом', subtitle: parts.join(' · '), badge: s ? `${s.severity} / 10` : null };
    }
    case HealthNoteKind.Metric: {
      const m = note.metric;
      const def = metrics.find((d) => d.code === m?.code);
      const name = def?.name ?? m?.code ?? 'Замер';
      const value = m ? metricValueText(m.value, m.value2) : '';
      push(def?.unit);
      push(note.text);
      return { title: `${name} ${value}`.trim(), subtitle: parts.join(' · '), badge: null };
    }
    case HealthNoteKind.Wellbeing: {
      const w = note.wellbeing;
      push((w?.factors ?? []).map((f) => WELLBEING_FACTOR_LABELS[f] ?? f).join(', ').toLowerCase());
      push(note.text);
      return {
        title: `Самочувствие: ${w ? wellbeingLevel(w.score).label.toLowerCase() : ''}`.trim(),
        subtitle: parts.join(' · '),
        badge: null,
      };
    }
    case HealthNoteKind.MedicationIntake: {
      push(note.intake?.dose);
      push(note.text);
      return { title: note.title ?? 'Лекарство', subtitle: parts.join(' · '), badge: null };
    }
    case HealthNoteKind.Sleep: {
      const s = note.sleep;
      if (s) push(`качество: ${(SLEEP_QUALITY_LABELS[s.quality] ?? '').toLowerCase()}`);
      push(note.text);
      return { title: s ? `Сон ${formatDuration(sleepMinutes(s))}` : 'Сон', subtitle: parts.join(' · '), badge: null };
    }
    default:
      return { title: note.text ?? 'Заметка', subtitle: '', badge: null };
  }
}
