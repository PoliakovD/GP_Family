/** Строка построчного сравнения: same — без изменений, removed — была только слева, added — только справа. */
export interface TextDiffLine {
  kind: 'same' | 'removed' | 'added';
  text: string;
}

/**
 * Построчный дифф двух текстов (LCS) — для сравнения версий промптов в админке. Промпты — десятки строк,
 * квадратичная таблица тут дешевле любой библиотеки.
 */
export function diffLines(before: string, after: string): TextDiffLine[] {
  const a = before.split('\n');
  const b = after.split('\n');
  const n = a.length;
  const m = b.length;
  const lcs: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    }
  }

  const result: TextDiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      result.push({ kind: 'same', text: a[i] });
      i++;
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
      result.push({ kind: 'removed', text: a[i++] });
    } else {
      result.push({ kind: 'added', text: b[j++] });
    }
  }
  while (i < n) result.push({ kind: 'removed', text: a[i++] });
  while (j < m) result.push({ kind: 'added', text: b[j++] });
  return result;
}
