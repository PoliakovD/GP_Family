import type { ReviewKind, ReviewOrigin } from '../../../services/admin-api.service';

/** Подписи видов задач очереди «Одобрение». */
const KIND_LABELS: Record<ReviewKind, string> = {
  'lab-analyte': 'Показатель',
  medication: 'Препарат',
  'visit-medication': 'Препарат из заключения',
};

export function reviewKindLabel(kind: ReviewKind): string {
  return KIND_LABELS[kind] ?? kind;
}

const ORIGIN_LABELS: Record<ReviewOrigin, string> = {
  extraction: 'Распознан из документа',
  manual: 'Введён вручную',
  maintenance: 'Переобогащение (обслуживание)',
  medkit: 'Добавлен в аптечку',
  visit: 'Из заключения врача',
};

export function reviewOriginLabel(origin: ReviewOrigin): string {
  return ORIGIN_LABELS[origin] ?? origin;
}

/** «85%» либо «нет оценки» — отсутствие оценки модели тоже считается «ниже порога» (безопасный
 * дефолт на бэкенде), поэтому пустое значение показываем явно, а не прочерком. */
export function confidenceLabel(confidence: number | null | undefined): string {
  if (confidence === null || confidence === undefined) return 'нет оценки';
  return `${Math.round(confidence * 100)}%`;
}

/** Подписи верхнеуровневых ключей payload — для сводки отличий от текущей записи справочника. */
const FIELD_LABELS: Record<string, string> = {
  loincCode: 'LOINC',
  defaultUnit: 'Единица по умолчанию',
  plainExplanation: 'Простыми словами',
  whyMeasured: 'Зачем измеряют',
  highMeans: 'Что значит высокое',
  lowMeans: 'Что значит низкое',
  calculationInstructions: 'Методика расчёта',
  relatedNames: 'Что смотрят вместе',
  refRanges: 'Референсные диапазоны',
  internationalName: 'МНН',
  tradeNames: 'Торговые названия',
  form: 'Форма выпуска',
  purpose: 'Назначение',
  simplePurpose: 'Назначение простыми словами',
  usage: 'Способ применения и дозы',
  storage: 'Хранение',
  driving: 'Влияние на вождение',
  specialNotes: 'Противопоказания и рекомендации',
  schemaVersion: 'Версия схемы',
};

export function payloadFieldLabel(key: string): string {
  return FIELD_LABELS[key] ?? key;
}

/** Структурное сравнение (порядок ключей объектов не важен, порядок элементов массива важен). */
function stableStringify(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(',')}]`;
  if (value && typeof value === 'object') {
    const obj = value as Record<string, unknown>;
    return `{${Object.keys(obj)
      .sort()
      .map((k) => `${JSON.stringify(k)}:${stableStringify(obj[k])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value ?? null);
}

function parseObject(json: string | null | undefined): Record<string, unknown> {
  if (!json) return {};
  try {
    const parsed: unknown = JSON.parse(json);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

export interface PayloadFieldDiff {
  key: string;
  before: unknown;
  after: unknown;
}

/** Верхнеуровневые ключи, значения которых различаются между текущей записью справочника и
 * черновиком. schemaVersion не считается отличием (служебное поле). Без текущей записи (null) —
 * все непустые ключи черновика считаются «новыми». */
export function diffPayload(draftJson: string, currentJson: string | null): PayloadFieldDiff[] {
  const draft = parseObject(draftJson);
  const current = parseObject(currentJson);
  const keys = [...new Set([...Object.keys(current), ...Object.keys(draft)])].filter((k) => k !== 'schemaVersion');
  return keys
    .filter((k) => stableStringify(draft[k]) !== stableStringify(current[k]))
    .map((k) => ({ key: k, before: current[k] ?? null, after: draft[k] ?? null }));
}

/** Короткое строковое представление значения поля для сводки отличий. */
export function previewValue(value: unknown, maxLength = 160): string {
  if (value === null || value === undefined || value === '') return '—';
  let text: string;
  if (typeof value === 'string') text = value;
  else if (Array.isArray(value) && value.every((v) => typeof v === 'string')) text = value.join(', ');
  else if (Array.isArray(value)) text = `${value.length} шт.`;
  else text = JSON.stringify(value);
  return text.length > maxLength ? `${text.slice(0, maxLength)}…` : text;
}

/** Индекс элемента, который нужно открыть/выделить после удаления элемента `removedIndex` из списка
 * длиной `lengthBefore`: следующий на том же месте, а если удалили последний — предыдущий; -1, если
 * список опустел. */
export function indexAfterRemoval(removedIndex: number, lengthBefore: number): number {
  const lengthAfter = lengthBefore - 1;
  if (lengthAfter <= 0) return -1;
  return Math.min(removedIndex, lengthAfter - 1);
}

/** Клавиша горячей клавиши не должна срабатывать, пока админ печатает в поле ввода. */
export function isTypingTarget(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  if (!el || !el.tagName) return false;
  const tag = el.tagName.toLowerCase();
  return tag === 'input' || tag === 'textarea' || tag === 'select' || el.isContentEditable === true;
}
