// Отображение строки показателя (таблица на десктопе, карточки на мобиле) — чистые функции,
// вынесенные из MedicalRecordsPanelComponent. Шаблон вызывает их через одноимённые поля компонента.

import { IndicatorFlag, RefSource } from '../../models/types';
import type { IndicatorDto } from '../../models/types';
import { formatDeviation } from '../../shared/reference-scale/reference-scale.component';
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

/** Подсветка строки по статусу — зелёная/красная, ровно два состояния (не градация
 * Low/High/Critical) — тот же принцип, что палочка на шкале (см. reference-scale). */
export function rowStatusClass(ind: IndicatorDto): string {
  if (ind.flag === IndicatorFlag.Normal) return 'indicator-row-ok';
  if (ind.flag === IndicatorFlag.Unknown) return '';
  return 'indicator-row-bad';
}

/** Только окраска ячейки «Значение» — подпись статуса рендерит <app-status-chip>. */
export function flagClass(flag: number): string {
  switch (flag) {
    case IndicatorFlag.Low:
    case IndicatorFlag.High:
      return 'indicator-flag-warning';
    case IndicatorFlag.Critical:
      return 'indicator-flag-danger';
    case IndicatorFlag.Normal:
      return 'indicator-flag-ok';
    default:
      return 'indicator-flag-unknown';
  }
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
