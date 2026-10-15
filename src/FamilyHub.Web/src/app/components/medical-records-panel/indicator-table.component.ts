import { Component, computed, inject, input, output, signal, ChangeDetectionStrategy } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import type { IndicatorDto, UpdateIndicatorRequest } from '../../models/types';
import { ApiError, ApiService } from '../../services/api.service';
import { ReferenceScaleComponent } from '../../shared/reference-scale/reference-scale.component';
import { StatusChipComponent } from '../../shared/status-chip/status-chip.component';
import { ToastService } from '../../shared/toast/toast.service';
import { ClickableDirective } from '../../shared/util/clickable.directive';
import { enrichmentStatusTitle } from '../../shared/util/enrichment-status-text';
import { Plus } from 'lucide';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import {
  type IndicatorSortMode, type IndicatorStatusFilter, deviationAmount, indicatorLabel, indicatorReference,
  indicatorStatusGroup, indicatorSubtitle, isCalculatedRef, isInferredRef, groupIndicatorsByPanel, scaleBounds,
  scaleValue, sortIndicators,
} from './indicator-display';
import { emptyIndicatorForm, sanitizeIndicatorForm } from './indicator-form';

/**
 * Показатели одной записи-анализа (вынесено из MedicalRecordsPanelComponent): сортировка,
 * таблица на десктопе / карточки на узком экране, ручное добавление показателя. Клик по
 * показателю — `open` (панель справки открывает родитель); после добавления — `created`
 * (родитель перечитывает показатели и запись).
 */
@Component({
  selector: 'app-indicator-table',
  standalone: true,
  imports: [ClickableDirective, FormsModule, IconComponent, NgTemplateOutlet, ReferenceScaleComponent, StatusChipComponent],
  templateUrl: './indicator-table.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './indicator-table.component.scss',
})
export class IndicatorTableComponent {
  readonly recordId = input.required<string>();
  readonly indicators = input.required<readonly IndicatorDto[]>();
  /** Десктоп — таблица; иначе компактные карточки. */
  readonly wide = input(false);
  /** Счётчик-фильтр из шапки записи; null — все показатели. */
  readonly statusFilter = input<IndicatorStatusFilter | null>(null);

  readonly open = output<IndicatorDto>();
  readonly created = output<void>();

  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  /** Индикаторов обычно от единиц до пары десятков — сортируем на каждый пересчёт без мемоизации. */
  readonly sortMode = signal<IndicatorSortMode>('abnormal');
  private readonly visible = computed(() => {
    const filter = this.statusFilter();
    return filter ? this.indicators().filter((i) => indicatorStatusGroup(i) === filter) : this.indicators();
  });
  readonly sorted = computed(() => sortIndicators(this.visible(), this.sortMode()));
  /** Сколько показателей скрыто фильтром — подсказка под таблицей. */
  readonly filteredOut = computed(() => this.indicators().length - this.visible().length);

  /** short — на телефоне: три полные подписи не помещаются в 390px. */
  protected readonly sortOptions: { value: IndicatorSortMode; label: string; short: string }[] = [
    { value: 'abnormal', label: 'Сначала отклонения', short: 'Отклонения' },
    { value: 'form', label: 'Как в бланке', short: 'Как в бланке' },
    { value: 'alpha', label: 'По алфавиту', short: 'А–Я' },
  ];
  protected readonly icons = { Plus };
  /** Блоки по разделам бланка — только «Как в бланке»: в других режимах порядок уже не бланковый,
   * и подзаголовки разрезали бы список случайным образом. */
  readonly groups = computed(() => this.sortMode() === 'form'
    ? groupIndicatorsByPanel(this.sorted())
    : [{ label: null, items: this.sorted() }]);

  creating = false;
  form: UpdateIndicatorRequest = emptyIndicatorForm();
  saving = false;

  protected readonly deviationAmount = deviationAmount;
  protected readonly indicatorSubtitle = indicatorSubtitle;
  protected readonly indicatorReference = indicatorReference;
  protected readonly scaleBounds = scaleBounds;
  protected readonly scaleValue = scaleValue;
  protected readonly indicatorLabel = indicatorLabel;
  protected readonly isCalculatedRef = isCalculatedRef;
  protected readonly isInferredRef = isInferredRef;

  protected enrichmentTitle(ind: IndicatorDto): string {
    if (ind.enrichmentWaitingForAi) return 'ИИ недоступен — уточнение нормы продолжится автоматически, когда он вернётся';
    return enrichmentStatusTitle(
      ind.enrichmentQueueAhead, 'Справочник пока не знает норму — идёт фоновый поиск');
  }

  startCreate(): void {
    this.creating = true;
    this.form = emptyIndicatorForm();
  }

  cancelCreate(): void {
    this.creating = false;
    this.form = emptyIndicatorForm();
  }

  async saveNew(): Promise<void> {
    if (this.saving) return;
    if (!this.form.displayName.trim()) {
      this.toast.error('Укажите название показателя.');
      return;
    }
    this.saving = true;
    try {
      await this.api.createIndicator(this.recordId(), sanitizeIndicatorForm(this.form));
      this.cancelCreate();
      this.created.emit();
    } catch (err) {
      this.toast.error(err instanceof ApiError ? err.message : 'Не удалось добавить показатель — возможно, такой уже есть в записи.');
    } finally {
      this.saving = false;
    }
  }
}
