import { Gender } from '../../models/types';
import type { KbRefRangeDto } from '../../models/types';

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

// --- Строки блока «Нормы» в статье справочника: иконка группы + подпись + диапазон ---

/** Иконка группы, для которой действует диапазон нормы (Phosphor): беременные, дети, фаза цикла
 * — по категории популяции; иначе по полу (женщины/мужчины), без пола — «все». */
export function normGroupIcon(range: Pick<KbRefRangeDto, 'sex' | 'population'>): string {
  switch (range.population) {
    case 1: return 'ph-fill ph-baby-carriage';
    case 2: return 'ph-fill ph-baby';
    case 3: return 'ph-fill ph-drop';
  }
  if (range.sex === Gender.Female) return 'ph-fill ph-gender-female';
  if (range.sex === Gender.Male) return 'ph-fill ph-gender-male';
  return 'ph-fill ph-users';
}

/** «Женщины 18–60 лет», «Беременные, II триместр», «Дети до 18 лет», «Все». */
export function normGroupLabel(
  range: Pick<KbRefRangeDto, 'sex' | 'population' | 'populationDetail' | 'ageFrom' | 'ageTo'>,
): string {
  const sex = range.sex === Gender.Male ? 'Мужчины' : range.sex === Gender.Female ? 'Женщины' : 'Все';
  const detail = range.populationDetail ? `, ${range.populationDetail}` : '';
  let base: string;
  switch (range.population) {
    case 1: base = `Беременные${detail}`; break;
    case 2: base = `Дети${detail}`; break;
    case 3: base = `${sex}, фаза цикла${detail}`; break;
    default: base = sex;
  }

  const { ageFrom, ageTo } = range;
  const age =
    ageFrom !== null && ageTo !== null ? ` ${ageFrom}–${ageTo} лет`
      : ageFrom !== null ? ` от ${ageFrom} лет`
        : ageTo !== null ? ` до ${ageTo} лет`
          : '';
  return `${base}${age}`;
}

/** «120–140 г/л»; «—», если границы не заданы (качественная норма без числа). */
export function normValueLabel(range: Pick<KbRefRangeDto, 'low' | 'high' | 'unit'>): string {
  const fmt = (n: number) => (Math.round(n * 100) / 100).toString().replace('.', ',');
  const value = range.low !== null && range.high !== null ? `${fmt(range.low)}–${fmt(range.high)}` : '—';
  return `${value}${range.unit ? ' ' + range.unit : ''}`;
}
