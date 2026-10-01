/**
 * Название показателя для таблицы анализа — ПОЛНОЕ, как в бланке (displayName): «MCH (среднее содержание Hb в
 * эритроците)». Пользователь открывает анализ именно затем, чтобы прочитать его целиком.
 *
 * НЕ из ключа дедупликации (analyteKey): ключ сворачивает латиницу в кириллицу фонетически («MCV» → «мкв»,
 * «RDW» → «рдв», «MCH» → «мч»), и пользователь видел бы «Мкв», «Рдв», «Мч» вместо названия из бланка. Ключ — только
 * запасной вариант, если отображаемого имени почему-то нет.
 */
export function indicatorLabel(displayName: string | null | undefined, analyteKey: string): string {
  const full = (displayName ?? '').trim();
  if (full.length > 0) return full;

  const key = analyteKey.trim();
  return key.length > 0 ? key[0].toUpperCase() + key.slice(1) : '';
}
