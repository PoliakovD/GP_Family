// Русское склонение существительного по числительному — было приватным одноразовым методом в
// MedkitsPanelComponent.medicationCountLabel(); вынесено в редизайне v2, т.к. та же задача
// нужна ещё и на Главной («N лекарств истекает») и в группах «Анализов» («N анализов»).

/** count всегда положительное целое; для 0 используется форма "many" (согласуется с русским
 * "0 лекарств"). Пример: pluralizeRu(3, 'лекарство', 'лекарства', 'лекарств') → 'лекарства'. */
export function pluralizeRu(count: number, one: string, few: string, many: string): string {
  const mod100 = count % 100;
  const mod10 = count % 10;
  if (mod100 >= 11 && mod100 <= 14) return many;
  if (mod10 === 1) return one;
  if (mod10 >= 2 && mod10 <= 4) return few;
  return many;
}
