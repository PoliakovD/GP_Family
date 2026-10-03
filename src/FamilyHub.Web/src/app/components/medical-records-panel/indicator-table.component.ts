import { Component, computed, inject, input, output, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import type { IndicatorDto, UpdateIndicatorRequest } from '../../models/types';
import { ApiError, ApiService } from '../../services/api.service';
import { ReferenceScaleComponent } from '../../shared/reference-scale/reference-scale.component';
import { StatusChipComponent } from '../../shared/status-chip/status-chip.component';
import { ToastService } from '../../shared/toast/toast.service';
import { ClickableDirective } from '../../shared/util/clickable.directive';
import { enrichmentStatusTitle } from '../../shared/util/enrichment-status-text';
import {
  type IndicatorSortMode, deviationFor, flagClass, indicatorLabel, indicatorReference, isCalculatedRef, isInferredRef,
  rowStatusClass, scaleBounds, scaleValue, sortIndicators,
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
  imports: [ClickableDirective, FormsModule, NgTemplateOutlet, ReferenceScaleComponent, StatusChipComponent],
  templateUrl: './indicator-table.component.html',
  styleUrl: './indicator-table.component.scss',
})
export class IndicatorTableComponent {
  readonly recordId = input.required<string>();
  readonly indicators = input.required<readonly IndicatorDto[]>();
  /** Десктоп — таблица; иначе компактные карточки. */
  readonly wide = input(false);

  readonly open = output<IndicatorDto>();
  readonly created = output<void>();

  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  /** Индикаторов обычно от единиц до пары десятков — сортируем на каждый пересчёт без мемоизации. */
  readonly sortMode = signal<IndicatorSortMode>('abnormal');
  readonly sorted = computed(() => sortIndicators(this.indicators(), this.sortMode()));

  creating = false;
  form: UpdateIndicatorRequest = emptyIndicatorForm();
  saving = false;

  protected readonly rowStatusClass = rowStatusClass;
  protected readonly deviationFor = deviationFor;
  protected readonly flagClass = flagClass;
  protected readonly indicatorReference = indicatorReference;
  protected readonly scaleBounds = scaleBounds;
  protected readonly scaleValue = scaleValue;
  protected readonly indicatorLabel = indicatorLabel;
  protected readonly isCalculatedRef = isCalculatedRef;
  protected readonly isInferredRef = isInferredRef;

  protected enrichmentTitle(ind: IndicatorDto): string {
    if (ind.enrichmentWaitingForAi) return 'ИИ недоступен — уточнение нормы продолжится автоматически, когда он вернётся';
    return enrichmentStatusTitle(
      ind.enrichmentLiveText, ind.enrichmentQueueAhead, 'Справочник пока не знает норму — идёт фоновый поиск');
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
