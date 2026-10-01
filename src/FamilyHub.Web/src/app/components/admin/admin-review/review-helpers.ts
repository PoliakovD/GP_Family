import {
  KbChangeTarget,
  KbVerificationStatus,
  type KbChangeTargetValue,
  type KbVerificationStatusValue,
  type ReviewKind,
  type ReviewOrigin,
  type ReviewStage,
} from '../../../services/admin-api.service';

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

const STAGE_LABELS: Record<ReviewStage, string> = { search: 'Поиск', result: 'Результат' };

export function reviewStageLabel(stage: ReviewStage): string {
  return STAGE_LABELS[stage] ?? stage;
}

/** Внутренний статус проверки записи справочника — только админ-UI (ADR-0018). */
const VERIFICATION_LABELS: Record<KbVerificationStatusValue, string> = {
  [KbVerificationStatus.AiUnverified]: 'Не проверено',
  [KbVerificationStatus.AdminVerified]: 'Проверено',
  [KbVerificationStatus.AdminEdited]: 'Проверено с правками',
  [KbVerificationStatus.ManualKnowledge]: 'Знание эксперта',
};

export function verificationLabel(status: KbVerificationStatusValue, stale = false): string {
  const base = VERIFICATION_LABELS[status] ?? 'Не проверено';
  return stale && status !== KbVerificationStatus.AiUnverified ? `${base} (устарело)` : base;
}

/** Класс тега под статус: непроверенное и устаревшее — предупреждение, проверенное — зелёное. */
export function verificationTagClass(status: KbVerificationStatusValue, stale = false): string {
  if (status === KbVerificationStatus.AiUnverified) return 'tag-warning';
  if (stale) return 'tag-warning';
  return status === KbVerificationStatus.ManualKnowledge ? 'tag-accent' : 'tag-ok';
}

const HISTORY_ACTION_LABELS: Record<string, string> = {
  'ai-write': 'Запись ИИ',
  'admin-edit': 'Правка админа',
  'admin-delete': 'Удаление',
  'admin-merge': 'Объединение',
  verify: 'Отмечено проверенным',
  revert: 'Откат',
  'cache-add': 'Источник добавлен',
  'cache-remove': 'Источник удалён',
  'cache-pin': 'Закрепление источника',
  'cache-override': 'Включение/выключение источника',
  'cache-replace': 'Набор источников заменён',
  'cache-merge': 'Слияние кэша',
};

export function historyActionLabel(action: string): string {
  return HISTORY_ACTION_LABELS[action] ?? action;
}

const TARGET_LABELS: Record<KbChangeTargetValue, string> = {
  [KbChangeTarget.LabAnalyteKb]: 'Показатель',
  [KbChangeTarget.MedicationKb]: 'Препарат',
  [KbChangeTarget.LabAnalyteSearchCache]: 'Кэш поиска (показатель)',
  [KbChangeTarget.MedicationSearchCache]: 'Кэш поиска (препарат)',
};

export function historyTargetLabel(target: KbChangeTargetValue): string {
  return TARGET_LABELS[target] ?? String(target);
}

/** Как показать происхождение источника: ручной (цитата/знание эксперта) либо домен. */
export function sourceKindLabel(kind: string | null, origin: 'Auto' | 'Manual'): string | null {
  if (kind === 'expert-knowledge') return 'знание эксперта';
  if (kind === 'manual-quote' || origin === 'Manual') return 'ручная цитата';
  return null;
}

export interface HistoryDiffLine {
  label: string;
  before: string;
  after: string;
}

function parseSnapshot(json: string | null): Record<string, unknown> | null {
  if (!json) return null;
  try {
    const parsed: unknown = JSON.parse(json);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function parseSnippets(json: unknown): { url: string; title: string; pinned: boolean; origin: string }[] {
  if (typeof json !== 'string') return [];
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed)
      ? parsed.map((s) => {
          const o = (s ?? {}) as Record<string, unknown>;
          return {
            url: String(o['url'] ?? ''), title: String(o['title'] ?? ''),
            pinned: o['pinned'] === true, origin: String(o['origin'] ?? 'Auto'),
          };
        })
      : [];
  } catch {
    return [];
  }
}

/** Сводка «было → стало» по двум снимкам журнала (ADR-0018): для строки справочника — имя, источник, синонимы, статус
 * проверки и изменённые поля payload; для строки кэша — добавленные/удалённые/изменённые сниппеты и override'ы. */
export function diffSnapshots(beforeJson: string | null, afterJson: string | null): HistoryDiffLine[] {
  const before = parseSnapshot(beforeJson);
  const after = parseSnapshot(afterJson);
  const lines: HistoryDiffLine[] = [];
  const probe = after ?? before;
  if (!probe) return lines;

  if ('payloadJson' in probe || 'displayName' in probe) {
    const text = (o: Record<string, unknown> | null, key: string): string => (o && o[key] != null ? String(o[key]) : '—');
    const list = (o: Record<string, unknown> | null, key: string): string =>
      o && Array.isArray(o[key]) && (o[key] as unknown[]).length > 0 ? (o[key] as unknown[]).join(', ') : '—';
    if (text(before, 'displayName') !== text(after, 'displayName'))
      lines.push({ label: 'Название', before: text(before, 'displayName'), after: text(after, 'displayName') });
    if (text(before, 'source') !== text(after, 'source'))
      lines.push({ label: 'Источник знания', before: text(before, 'source'), after: text(after, 'source') });
    if (list(before, 'aliases') !== list(after, 'aliases'))
      lines.push({ label: 'Синонимы', before: list(before, 'aliases'), after: list(after, 'aliases') });
    if (list(before, 'lockedFields') !== list(after, 'lockedFields'))
      lines.push({ label: 'Залочено', before: list(before, 'lockedFields'), after: list(after, 'lockedFields') });
    const statusOf = (o: Record<string, unknown> | null): string =>
      o && typeof o['verificationStatus'] === 'number' ? verificationLabel(o['verificationStatus'] as KbVerificationStatusValue) : '—';
    if (statusOf(before) !== statusOf(after)) lines.push({ label: 'Статус проверки', before: statusOf(before), after: statusOf(after) });
    const afterPayload = after ? String(after['payloadJson'] ?? '{}') : '{}';
    const beforePayload = before ? String(before['payloadJson'] ?? '{}') : null;
    for (const d of diffPayload(afterPayload, beforePayload))
      lines.push({ label: payloadFieldLabel(d.key), before: previewValue(d.before), after: previewValue(d.after) });
    return lines;
  }

  if ('snippetsJson' in probe) {
    const b = parseSnippets(before?.['snippetsJson']);
    const a = parseSnippets(after?.['snippetsJson']);
    const byUrl = (list: typeof a) => new Map(list.map((s) => [s.url, s]));
    const bm = byUrl(b);
    const am = byUrl(a);
    for (const s of a) if (!bm.has(s.url)) lines.push({ label: 'Источник добавлен', before: '—', after: s.title || s.url });
    for (const s of b) if (!am.has(s.url)) lines.push({ label: 'Источник удалён', before: s.title || s.url, after: '—' });
    for (const s of a) {
      const old = bm.get(s.url);
      if (old && old.pinned !== s.pinned)
        lines.push({ label: 'Закрепление', before: old.pinned ? 'закреплён' : 'нет', after: s.pinned ? 'закреплён' : 'нет' });
    }
    const ov = (o: Record<string, unknown> | null): string => String(o?.['overridesJson'] ?? '—');
    if (ov(before) !== ov(after)) lines.push({ label: 'Включение/выключение источников', before: ov(before), after: ov(after) });
    return lines;
  }

  return lines;
}

/** Значение порога из поля ввода: число в [0..1] либо null (пусто/вне диапазона). Запятая как десятичный разделитель допустима. */
export function parseThreshold(raw: string | number | null | undefined): number | null {
  if (raw === null || raw === undefined || raw === '') return null;
  const value = typeof raw === 'number' ? raw : Number(String(raw).replace(',', '.'));
  return Number.isFinite(value) && value >= 0 && value <= 1 ? value : null;
}
