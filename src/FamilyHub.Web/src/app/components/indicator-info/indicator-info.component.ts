import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type {
  IndicatorHistoryPoint, KbAnalyteCard, KbRefRangeDto, PatientContextDto, UpdateIndicatorRequest,
} from '../../models/types';
import { Gender, IndicatorFlag } from '../../models/types';
import { ReferenceScaleComponent, formatDeviation } from '../../shared/reference-scale/reference-scale.component';
import { StatusChipComponent } from '../../shared/status-chip/status-chip.component';
import { SparklineComponent, SparklinePoint } from '../../shared/sparkline/sparkline.component';
import { ExpandableComponent } from '../../shared/expandable/expandable.component';
import { normGroupIcon, normGroupLabel, normValueLabel } from '../../shared/util/lab-norm';
import { formatDayMonthYear } from '../../shared/util/date-format';
import { pluralizeRu } from '../../shared/util/pluralize';

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
    imports: [FormsModule, ReferenceScaleComponent, StatusChipComponent, SparklineComponent, ExpandableComponent],
    templateUrl: './indicator-info.component.html',
    styleUrl: './indicator-info.component.scss'
})
export class IndicatorInfoComponent {
  readonly article = input<KbAnalyteCard | null>(null);
  /** Заголовок, пока статья не привязана (article=null) — имя показателя из самого бланка. */
  readonly displayName = input<string>('');
  readonly reading = input<IndicatorInfoReading | null>(null);
  readonly history = input<IndicatorHistoryPoint[] | null>(null);
  /** Редизайн v2.2 — GET /api/indicators/{id}/article уже отдаёт возраст/пол пациента на дату
   * записи (см. PatientContextDto), раньше просто игнорировался (openIndicatorInfo не читал
   * response.patient). displayName-имя пациента — отдельным входом, DTO его не содержит. */
  readonly patient = input<PatientContextDto | null>(null);
  readonly patientName = input<string>('');

  /** Редизайн v2.2 — редактирование/удаление показателя переехали сюда из таблицы записи
   * (medical-records-panel), где раньше жили карандаш/корзина в отдельной колонке + инлайн-форма
   * в строке. Мутационная логика (startEditIndicator/saveEditIndicator/deleteIndicatorRow)
   * остаётся у родителя — только у него есть recordId/refresh/confirm-диалог; сюда переехала
   * только сама форма. canEdit=false у безличной статьи справочника (чип "что смотрят вместе",
   * каталог /health/kb/indicators) — там нет ни indicatorId, ни recordId, редактировать нечего. */
  readonly canEdit = input(false);
  readonly editing = input(false);
  /** Родительский объект передаётся по ссылке — [(ngModel)] на его полях мутирует его на месте
   * (тот же приём, что раньше был у инлайн-формы прямо в таблице), поэтому saveEdit не должен
   * нести значение формы отдельно. */
  readonly editForm = input<UpdateIndicatorRequest | null>(null);
  readonly saving = input(false);
  readonly startEdit = output<void>();
  readonly cancelEdit = output<void>();
  readonly saveEdit = output<void>();
  readonly requestDelete = output<void>();

  /** Клик по кликабельному чипу "что смотрят вместе" — id статьи, открыть её тем же путём. */
  readonly openRelated = output<string>();
  /** Футер "Открыть в справочнике" — только когда есть персональный reading (иначе мы уже в
   * справочнике/на статье по чипу). */
  readonly openInCatalog = output<void>();

  /** Источник показан только когда он известен (ключ справочника — (показатель, источник), см.
   * GlobalLabAnalyteKb.SpecimenKbId) — сервер уже отдаёт готовую подпись, ссылку локально не
   * разбираем (пересборка enrich-пайплайна, никакой фиксированной классификации на фронте). */
  readonly specimenLabel = computed(() => this.article()?.specimenDisplayName ?? null);

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

  /** «Норма подобрана для Валерии: 28 лет, жен.» — рядом с текущим значением, только когда есть
   * и возраст/пол пациента, и подобранный диапазон (иначе непонятно, к чему относится подпись). */
  readonly patientContextLabel = computed(() => {
    const p = this.patient();
    if (!p || (p.ageYears === null && p.sex === null) || !this.matchedRange()) return null;
    const parts: string[] = [];
    if (p.ageYears !== null) parts.push(`${p.ageYears} ${pluralizeRu(p.ageYears, 'год', 'года', 'лет')}`);
    if (p.sex !== null) parts.push(p.sex === Gender.Male ? 'муж.' : 'жен.');
    return `Норма подобрана для ${this.patientName() || 'пациента'}: ${parts.join(', ')}`;
  });

  readonly hasEnoughHistory = computed(() => (this.history()?.length ?? 0) >= 2);

  /** Редизайн v2.2 — «Динамика» сворачивается по умолчанию и открывается кнопкой (см. шаблон),
   * вместо того чтобы всегда показывать sparkline. history() уже строго по этому же пациенту
   * (см. ExtractionQueryService.QueryVisibleHistoryAsync — фильтр по FamilyDependentId/
   * TargetUserId) — кнопка только переключает видимость, новый запрос не заводит. */
  readonly dynamicsOpen = signal(false);

  readonly sparklinePoints = computed<SparklinePoint[]>(() =>
    (this.history() ?? [])
      .filter((p) => p.valueNumericText !== null)
      .map((p) => ({ value: parseFloat(p.valueNumericText!), flag: p.flag })),
  );

  readonly IndicatorFlag = IndicatorFlag;
  readonly normGroupIcon = normGroupIcon;
  readonly normGroupLabel = normGroupLabel;
  readonly normValueLabel = normValueLabel;

  /** «120–140 г/л» для строки под шкалой — по подобранному диапазону пациента. */
  readonly normText = computed(() => {
    const r = this.matchedRange();
    return r ? normValueLabel(r) : '';
  });

  /** ISO-дата обновления статьи -> «12 июня 2026» (локаль ru в приложении не подключена). */
  updatedText(iso: string): string {
    return formatDayMonthYear(iso.slice(0, 10));
  }

  /** Домен-источник, выигравший при merge по приоритету (см. ReferenceRangeMerger) — null для
   * строк, записанных до пересборки enrich-пайплайна. */
  sourceLabel(r: KbRefRangeDto): string | null {
    return r.sourceDomain;
  }
}
