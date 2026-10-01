/**
 * Короткое имя показателя для строки таблицы анализа (полное имя из бланка показывается при раскрытии строки).
 *
 * Строится из ОТОБРАЖАЕМОГО имени (displayName), а не из ключа дедупликации (analyteKey): ключ сворачивает латиницу
 * в кириллицу фонетически («MCV» → «мкв», «RDW» → «рдв», «MCH» → «мч»), и пользователь видел бы «Мкв», «Рдв», «Мч»
 * вместо аббревиатур из бланка. Из имени убираются только пояснения в скобках и единицы измерения — то же, что режет
 * ключ на бэкенде (LabAnalyteNormalizer.Normalize), но без смены регистра и алфавита:
 * «MCH (среднее содержание Hb в эритроците)» → «MCH», «Гемоглобин (HGB), г/л» → «Гемоглобин».
 */
export function indicatorShortName(displayName: string | null | undefined, analyteKey: string): string {
  const full = (displayName ?? '').trim();
  if (full.length > 0) {
    const short = full
      .replace(/\([^)]*\)/g, ' ') // «(HGB)», «(среднее содержание …)»
      .replace(/,?\s*[×x]\s*10\s*\^?\s*\d+\s*\/\s*(?:л|мл)(?![\p{L}\p{N}])/giu, ' ') // счёт клеток «×10^9/л»
      .replace(/,?\s*(?:г|мг|мкг|нг|моль|ммоль|мкмоль|ед|ме|iu)\s*\/\s*(?:л|мл)(?![\p{L}\p{N}])/giu, ' ') // «г/л», «ммоль/л»
      .replace(/,\s*[^,]*(?:\/|%)[^,]*$/u, ' ') // прочий хвост-единица после запятой: «, мм/ч», «, %»
      .replace(/[\s,:;-]+$/u, '')
      .replace(/\s+/g, ' ')
      .trim();
    return short.length > 0 ? short : full;
  }

  const key = analyteKey.trim();
  return key.length > 0 ? key[0].toUpperCase() + key.slice(1) : '';
}
