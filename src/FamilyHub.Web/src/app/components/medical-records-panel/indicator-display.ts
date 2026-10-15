// Отображение строки показателя (таблица на десктопе, карточки на мобиле) — чистые функции,
// вынесенные из MedicalRecordsPanelComponent. Шаблон вызывает их через одноимённые поля компонента.

import { IndicatorFlag, RefSource } from '../../models/types';
import type { IndicatorDto } from '../../models/types';
import { formatDeviation, formatScaleNumber } from '../../shared/reference-scale/reference-scale.component';
import { indicatorLabel as indicatorLabelOf } from '../../shared/util/indicator-name';
import { specimenLabel } from '../../shared/util/specimen';

export type IndicatorSortMode = 'abnormal' | 'form' | 'alpha';

/** Название показателя для строки таблицы — ПОЛНОЕ, как в бланке («MCH (среднее содержание Hb в
 * эритроците)»). НЕ из analyteKey: ключ сворачивает латиницу в кириллицу фонетически («MCV» → «мкв»). */
export function indicatorLabel(indicator: IndicatorDto): string {
  return indicatorLabelOf(indicator.displayName, indicator.analyteKey);
}

/** Сортировка строк таблицы показателей. Возвращает новый массив.
 * - 'abnormal' — сначала не-норма; стабильная сортировка, внутри групп порядок из бланка;
 * - 'alpha' — по названию;
 * - 'form' — как пришло с сервера (порядок из бланка). */
export function sortIndicators(items: readonly IndicatorDto[], mode: IndicatorSortMode): IndicatorDto[] {
  const sorted = [...items];
  if (mode === 'alpha') {
    sorted.sort((a, b) => indicatorLabel(a).localeCompare(indicatorLabel(b), 'ru'));
  } else if (mode === 'abnormal') {
    sorted.sort((a, b) => Number(a.flag === IndicatorFlag.Normal) - Number(b.flag === IndicatorFlag.Normal));
  }
  return sorted;
}

/** Подряд идущие показатели одного раздела бланка; label null — без подзаголовка. */
export interface IndicatorGroup {
  label: string | null;
  items: IndicatorDto[];
}

/** Подзаголовок блока без раздела, идущего после блока с разделом (тот же текст, что в отчёте врачу). */
export const NO_PANEL_LABEL = 'Прочие показатели';

/** Разбивка на блоки по разделу бланка (panelLabel) — для режима «Как в бланке»: порядок не меняется,
 * подряд идущие показатели одного раздела — один блок. Без единого раздела — один блок без заголовка
 * (таблица как раньше). Блок без раздела в начале — без заголовка, после раздела — «Прочие показатели». */
export function groupIndicatorsByPanel(items: readonly IndicatorDto[]): IndicatorGroup[] {
  if (!items.some(i => i.panelLabel)) return [{ label: null, items: [...items] }];
  const groups: IndicatorGroup[] = [];
  let current: IndicatorGroup | null = null;
  let currentPanel: string | null = null;
  for (const item of items) {
    const panel = item.panelLabel || null;
    if (current === null || panel !== currentPanel) {
      currentPanel = panel;
      current = { label: panel ?? (groups.length === 0 ? null : NO_PANEL_LABEL), items: [] };
      groups.push(current);
    }
    current.items.push(item);
  }
  return groups;
}

/** Счётчик-фильтр в шапке записи: вне нормы / в норме / без нормы (DESIGN §3 «Анализ»). */
export type IndicatorStatusFilter = 'bad' | 'ok' | 'none';

export function indicatorStatusGroup(ind: IndicatorDto): IndicatorStatusFilter {
  if (ind.flag === IndicatorFlag.Normal) return 'ok';
  if (ind.flag === IndicatorFlag.Unknown) return 'none';
  return 'bad';
}

/** Насколько значение вышло за границу нормы — «4», «2,26» для бейджа «↑ выше на 4»; null — в норме
 * или сравнить не с чем (нет числа или нужной границы). */
export function deviationAmount(ind: IndicatorDto): string | null {
  const v = scaleValue(ind);
  if (v === null || Number.isNaN(v)) return null;
  const low = ind.refLowText ? parseFloat(ind.refLowText) : null;
  const high = ind.refHighText ? parseFloat(ind.refHighText) : null;
  if (low !== null && v < low) return formatScaleNumber(low - v);
  if (high !== null && v > high) return formatScaleNumber(v - high);
  return null;
}

/** Подпись под названием показателя: как он напечатан в бланке («СОЭ по Панченкову»), а если шкалы
 * нет — норма текстом; null — подписи нет. */
export function indicatorSubtitle(ind: IndicatorDto): string | null {
  if (ind.rawDisplayName && ind.rawDisplayName !== ind.displayName) return ind.rawDisplayName;
  return scaleBounds(ind) ? null : indicatorReference(ind);
}

/** Норма текстом: «3,5–5,0», «< 5», «> 1» или качественная (refText). null — нормы нет. */
export function indicatorReference(indicator: IndicatorDto): string | null {
  if (indicator.refText) return indicator.refText;
  if (indicator.refLowText && indicator.refHighText) return `${indicator.refLowText}–${indicator.refHighText}`;
  if (indicator.refHighText) return `< ${indicator.refHighText}`;
  if (indicator.refLowText) return `> ${indicator.refLowText}`;
  return null;
}

/** Числовые границы для <app-reference-scale> — только когда ОБЕ границы заданы (RefLowText/
 * RefHighText — InvariantCulture double либо null, parseFloat без нормализации запятых). */
export function scaleBounds(indicator: IndicatorDto): { low: number; high: number } | null {
  if (!indicator.refLowText || !indicator.refHighText) return null;
  return { low: parseFloat(indicator.refLowText), high: parseFloat(indicator.refHighText) };
}

export function scaleValue(indicator: IndicatorDto): number | null {
  return indicator.valueNumericText !== null ? parseFloat(indicator.valueNumericText) : null;
}

/** Подпись под шкалой («ниже нормы на 0,8»). */
export function deviationFor(ind: IndicatorDto, bounds: { low: number; high: number }): string | null {
  const v = scaleValue(ind);
  return v === null ? null : formatDeviation(v, bounds.low, bounds.high);
}

export function specimenLabelFor(indicator: { specimenDisplayName: string | null }): string {
  return specimenLabel(indicator.specimenDisplayName);
}

/** Норма рассчитана локальной LLM по методике справочника под возраст/пол (RefSource.KbCalculated). */
export function isCalculatedRef(indicator: IndicatorDto): boolean {
  return indicator.refSource === RefSource.KbCalculated;
}

/** Норма предположена моделью по общемедицинским знаниям — наименее надёжный шаг каскада
 * (RefSource.Inferred): ни бланк, ни справочник ответа не дали. */
export function isInferredRef(indicator: IndicatorDto): boolean {
  return indicator.refSource === RefSource.Inferred;
}
