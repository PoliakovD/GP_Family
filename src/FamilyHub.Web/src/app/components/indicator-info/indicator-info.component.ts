import { Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import type { IndicatorHistoryPoint, KbAnalyteCard, KbRefRangeDto } from '../../models/types';
import { Gender, SpecimenType } from '../../models/types';
import { ReferenceScaleComponent, formatDeviation } from '../../shared/reference-scale/reference-scale.component';
import { StatusChipComponent } from '../../shared/status-chip/status-chip.component';
import { SparklineComponent, SparklinePoint } from '../../shared/sparkline/sparkline.component';
import { ExpandableComponent } from '../../shared/expandable/expandable.component';
import { labPopulationLabel, shouldShowPopulationBadge } from '../../shared/util/lab-norm';
import { specimenLabel } from '../../shared/util/specimen';

/** Текущее значение показателя в контексте конкретной записи — есть только у первой из трёх
 * точек входа (клик по строке записи); у чипа «что смотрят вместе» и каталога reading=null. */
export interface IndicatorInfoReading {
  valueRaw: string;
  valueNumeric: number | null;
  unit: string | null;
  flag: number; // IndicatorFlag
  matchedRefRangeIndex: number | null;
}

/**
 * Тело статьи справочника показателя (редизайн v2, PR4) — Panel без своего URL, три точки
 * входа монтируют его по-разному (см. medical-records-panel/kb-analyte-tab):
 * 1) клик по показателю записи — reading+history заданы, article может быть null («справка ещё
 *    не заполнена», но панель всё равно открывается — значение+шкала есть всегда);
 * 2) чип «что смотрят вместе» — только article, без reading/history;
 * 3) каталог /health/kb/indicators — то же, что (2).
 */
@Component({
  selector: 'app-indicator-info',
  standalone: true,
  imports: [DatePipe, ReferenceScaleComponent, StatusChipComponent, SparklineComponent, ExpandableComponent],
  templateUrl: './indicator-info.component.html',
})
export class IndicatorInfoComponent {
  readonly article = input<KbAnalyteCard | null>(null);
  /** Заголовок, пока статья не привязана (article=null) — имя показателя из самого бланка. */
  readonly displayName = input<string>('');
  readonly reading = input<IndicatorInfoReading | null>(null);
  readonly history = input<IndicatorHistoryPoint[] | null>(null);

  /** Клик по кликабельному чипу "что смотрят вместе" — id статьи, открыть её тем же путём. */
  readonly openRelated = output<string>();
  /** Футер "Открыть в справочнике" — только когда есть персональный reading (иначе мы уже в
   * справочнике/на статье по чипу). */
  readonly openInCatalog = output<void>();

  readonly title = computed(() => this.article()?.displayName ?? this.displayName());

  /** Биоматериал показан только когда он известен (ключ справочника — (показатель, биоматериал),
   * см. GlobalLabAnalyteKb.Specimen) — Unknown не несёт полезной информации в заголовке статьи. */
  readonly specimenLabel = computed(() => {
    const specimen = this.article()?.specimen;
    return specimen !== undefined && specimen !== SpecimenType.Unknown ? specimenLabel(specimen) : null;
  });

  readonly matchedRange = computed<KbRefRangeDto | null>(() => {
    const a = this.article();
    const idx = this.reading()?.matchedRefRangeIndex;
    if (!a || idx === null || idx === undefined) return null;
    return a.refRanges[idx] ?? null;
  });

  readonly otherRanges = computed<KbRefRangeDto[]>(() => {
    const a = this.article();
    if (!a) return [];
    const idx = this.reading()?.matchedRefRangeIndex;
    return idx === null || idx === undefined ? a.refRanges : a.refRanges.filter((_, i) => i !== idx);
  });

  readonly scaleLow = computed(() => this.matchedRange()?.low ?? null);
  readonly scaleHigh = computed(() => this.matchedRange()?.high ?? null);

  readonly deviationLabel = computed(() => {
    const r = this.reading();
    if (!r || r.valueNumeric === null) return null;
    return formatDeviation(r.valueNumeric, this.scaleLow(), this.scaleHigh());
  });

  readonly hasEnoughHistory = computed(() => (this.history()?.length ?? 0) >= 2);

  readonly sparklinePoints = computed<SparklinePoint[]>(() =>
    (this.history() ?? [])
      .filter((p) => p.valueNumericText !== null)
      .map((p) => ({ value: parseFloat(p.valueNumericText!), flag: p.flag })),
  );

  rangeLabel(r: KbRefRangeDto): string {
    const sex = r.sex === Gender.Male ? 'Мужчины' : r.sex === Gender.Female ? 'Женщины' : 'Все';
    const age =
      r.ageFrom !== null && r.ageTo !== null
        ? `, ${r.ageFrom}–${r.ageTo} лет`
        : r.ageFrom !== null
          ? `, от ${r.ageFrom} лет`
          : r.ageTo !== null
            ? `, до ${r.ageTo} лет`
            : '';
    const value = r.low !== null && r.high !== null ? `${formatNum(r.low)}–${formatNum(r.high)}` : '—';
    return `${sex}${age}: ${value}${r.unit ? ' ' + r.unit : ''}`;
  }

  /** Бейдж категории популяции — только для особых случаев (беременность/дети/фаза цикла),
   * General не показывается (подразумевается по умолчанию). */
  populationBadge(r: KbRefRangeDto): string | null {
    return shouldShowPopulationBadge(r.population) ? labPopulationLabel(r.population, r.populationDetail) : null;
  }

  /** Домен-источник, выигравший при merge по приоритету (см. ReferenceRangeMerger) — null для
   * строк, записанных до пересборки enrich-пайплайна. */
  sourceLabel(r: KbRefRangeDto): string | null {
    return r.sourceDomain;
  }
}

function formatNum(n: number): string {
  return (Math.round(n * 100) / 100).toString().replace('.', ',');
}
