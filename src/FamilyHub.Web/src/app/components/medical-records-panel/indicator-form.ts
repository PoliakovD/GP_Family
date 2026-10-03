import type { UpdateIndicatorRequest } from '../../models/types';

export function emptyIndicatorForm(): UpdateIndicatorRequest {
  return {
    displayName: '', valueRaw: '', unit: null,
    refLowText: null, refHighText: null, refText: null,
  };
}

/** Обрезка пробелов + пустая строка → null — общий шаг перед отправкой формы показателя
 * (правка и создание используют одну и ту же форму). */
export function sanitizeIndicatorForm(form: UpdateIndicatorRequest): UpdateIndicatorRequest {
  return {
    displayName: form.displayName.trim(),
    valueRaw: form.valueRaw.trim(),
    unit: form.unit?.trim() || null,
    refLowText: form.refLowText?.trim() || null,
    refHighText: form.refHighText?.trim() || null,
    refText: form.refText?.trim() || null,
  };
}
