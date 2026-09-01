/** Человекочитаемые подписи типа нормы/категории популяции (пересборка enrich-пайплайна) — см.
 * FamilyHub.Domain.Enums.LabNormKind/LabPopulation, KbRefRangeDto.normKind/population. */

export const LAB_NORM_KIND_LABELS: Record<number, string> = {
  0: 'Диапазон',
  1: 'Расчётная',
  2: 'Качественная',
};

export function labNormKindLabel(normKind: number): string {
  return LAB_NORM_KIND_LABELS[normKind] ?? 'Диапазон';
}

export const LAB_POPULATION_LABELS: Record<number, string> = {
  0: 'Общая',
  1: 'Беременность',
  2: 'Дети',
  3: 'Фаза цикла',
};

export function labPopulationLabel(population: number, populationDetail: string | null): string {
  const base = LAB_POPULATION_LABELS[population] ?? 'Общая';
  return populationDetail ? `${base} (${populationDetail})` : base;
}

/** Общая норма (population=0) не показывается бейджем — она подразумевается по умолчанию,
 * бейдж нужен только чтобы выделить особые случаи (беременность, дети, фаза цикла). */
export function shouldShowPopulationBadge(population: number): boolean {
  return population !== 0;
}
