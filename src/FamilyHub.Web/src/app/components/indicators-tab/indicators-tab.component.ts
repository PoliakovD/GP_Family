import { Component, OnInit, inject } from '@angular/core';
import { ApiService, ApiError } from '../../services/api.service';
import { IndicatorFlag } from '../../models/types';
import type { IndicatorHistoryPoint, MyIndicatorSummary, UserSpecimen } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SparklineComponent, SparklinePoint } from '../../shared/sparkline/sparkline.component';
import { specimenLabelWithCustom } from '../../shared/util/specimen';

/**
 * Page (таксономия — patterns/frontend_web.md): «мои показатели» — последнее значение по каждому
 * лабораторному показателю среди СОБСТВЕННЫХ записей владельца (задачи 5.2/5.3), вкладка
 * «Показатели» хаба «Здоровье». Клик по строке открывает историю со спарклайном
 * (GET /api/indicators/{analyteKey}) — тренд считается на лету, без отдельного хранения.
 */
@Component({
  selector: 'app-indicators-tab',
  standalone: true,
  imports: [LoadingSpinnerComponent, BottomSheetComponent, SparklineComponent],
  templateUrl: './indicators-tab.component.html',
  styleUrl: './indicators-tab.component.scss',
})
export class IndicatorsTabComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly IndicatorFlag = IndicatorFlag;

  loading = true;
  error: string | null = null;
  items: MyIndicatorSummary[] = [];
  private customSpecimens: UserSpecimen[] = [];

  detailOpen = false;
  detailLoading = false;
  detailError: string | null = null;
  selected: MyIndicatorSummary | null = null;
  history: IndicatorHistoryPoint[] = [];

  async ngOnInit(): Promise<void> {
    this.loading = true;
    try {
      const [items, customSpecimens] = await Promise.all([this.api.getMyIndicators(), this.api.getSpecimens()]);
      this.items = items;
      this.customSpecimens = customSpecimens;
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить показатели.';
    } finally {
      this.loading = false;
    }
  }

  specimenLabel(item: { specimen: number; specimenCustomId: string | null }): string {
    return specimenLabelWithCustom(item.specimen, item.specimenCustomId, this.customSpecimens);
  }

  flagClass(flag: number): string {
    switch (flag) {
      case IndicatorFlag.Low:
      case IndicatorFlag.High:
        return 'indicator-flag-warning';
      case IndicatorFlag.Critical:
        return 'indicator-flag-danger';
      case IndicatorFlag.Normal:
        return 'indicator-flag-ok';
      default:
        return 'indicator-flag-unknown';
    }
  }

  /** Только точки с числовым значением — качественные результаты ("отрицательно" и т.п.) на
   * график не ложатся, но остаются видны в таблице истории под спарклайном. */
  get sparklinePoints(): SparklinePoint[] {
    return this.history
      .filter((p) => p.valueNumericText !== null)
      .map((p) => ({ value: Number(p.valueNumericText), flag: p.flag }));
  }

  async openDetail(item: MyIndicatorSummary): Promise<void> {
    this.detailOpen = true;
    this.detailLoading = true;
    this.detailError = null;
    this.selected = item;
    this.history = [];
    try {
      this.history = await this.api.getIndicatorHistory(item.analyteKey, item.specimen, item.specimenCustomId);
    } catch (err) {
      this.detailError = err instanceof ApiError ? err.message : 'Не удалось загрузить историю показателя.';
    } finally {
      this.detailLoading = false;
    }
  }

  closeDetail(): void {
    this.detailOpen = false;
  }
}
