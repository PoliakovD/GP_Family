// Общий клиентский текстовый фильтр — для разделов без серверного поискового источника
// (Дни рождения) и для второго уровня фильтрации внутри уже загруженного списка (аптечка внутри
// раскрытой аптечки). Не путать с DebouncedSearch (серверный поиск через /api/search).

/** Регистронезависимое, "ё"-нечувствительное сравнение подстроки. */
function normalize(value: string): string {
  return value.trim().toLowerCase().replace(/ё/g, 'е');
}

/**
 * true, если нормализованный query — подстрока хотя бы одного из полей. Пустой query совпадает
 * всегда (используется как "фильтр не активен"). Игнорирует null/undefined-поля.
 */
export function matchesQuery(query: string, ...fields: (string | null | undefined)[]): boolean {
  const q = normalize(query);
  if (!q) return true;
  return fields.some((f) => !!f && normalize(f).includes(q));
}
