import { Component, OnInit, inject } from '@angular/core';
import { ApiService, ApiError } from '../../services/api.service';
import { IndicatorFlag } from '../../models/types';
import type { IndicatorHistoryPoint, MyIndicatorSummary } from '../../models/types';
import { LoadingSpinnerComponent } from '../../shared/loading-spinner/loading-spinner.component';
import { BottomSheetComponent } from '../../shared/bottom-sheet/bottom-sheet.component';
import { SparklineComponent, SparklinePoint } from '../../shared/sparkline/sparkline.component';
import { PersonChipComponent } from '../../shared/person-chip/person-chip.component';
import type { AvatarPerson } from '../../shared/avatar/avatar.component';
import { specimenLabel } from '../../shared/util/specimen';
import { buildPatientOptions, type PatientOption } from '../../shared/util/patient-options';
import { shortenDisplayName, personAvatarPartsFromName } from '../../shared/util/person-name';
import { FamilyStateService } from '../../services/family-state.service';
import { AuthService } from '../../services/auth.service';

const ALL_PATIENTS_KEY = 'all';

/**
 * Page (таксономия — patterns/frontend_web.md): «мои показатели» — последнее значение по каждому
 * лабораторному показателю среди СОБСТВЕННЫХ записей владельца (задачи 5.2/5.3), вкладка
 * «Показатели анализов» хаба «Здоровье». Список и график ПОПАЦИЕНТНЫЕ (реальный баг: несколько человек
 * одной семьи сдавали один и тот же анализ — раньше схлопывались в одну строку/график, см.
 * class doc LabIndicator.FamilyDependentId на бэкенде) — тот же фильтр «Пациент», что уже есть в
 * панели записей (buildPatientOptions), плюс имя пациента прямо в строке списка. Клик по строке
 * открывает историю ИМЕННО этого пациента (GET /api/indicators/{analyteKey}?dependentId=&targetUserId=).
 */
@Component({
    selector: 'app-indicators-tab',
    imports: [LoadingSpinnerComponent, BottomSheetComponent, SparklineComponent, PersonChipComponent],
    templateUrl: './indicators-tab.component.html',
    styleUrl: './indicators-tab.component.scss'
})
export class IndicatorsTabComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly state = inject(FamilyStateService);
  private readonly auth = inject(AuthService);

  readonly IndicatorFlag = IndicatorFlag;

  loading = true;
  error: string | null = null;
  items: MyIndicatorSummary[] = [];
  patientFilterKey = ALL_PATIENTS_KEY;

  detailOpen = false;
  detailLoading = false;
  detailError: string | null = null;
  selected: MyIndicatorSummary | null = null;
  history: IndicatorHistoryPoint[] = [];

  async ngOnInit(): Promise<void> {
    this.loading = true;
    try {
      this.items = await this.api.getMyIndicators();
      this.error = null;
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить показатели.';
    } finally {
      this.loading = false;
    }
  }

  /** «Все» + тот же список пациентов, что уже показывает панель записей — один и тот же
   * составной ключ (dep:{id}/user:{id}/self), сравнивается со строкой из itemPatientKey(). */
  get patientFilterOptions(): PatientOption[] {
    return [
      { key: ALL_PATIENTS_KEY, familyDependentId: null, targetUserId: null, label: 'Все' },
      ...buildPatientOptions(this.state.activeFamilies(), this.auth.me()?.userId),
    ];
  }

  itemPatientKey(item: MyIndicatorSummary): string {
    if (item.familyDependentId) return `dep:${item.familyDependentId}`;
    if (item.targetUserId) return `user:${item.targetUserId}`;
    return 'self';
  }

  /** Редизайн v2.1 — app-person-chip в строке списка (было — сырое item.patientName текстом). */
  readonly shortenDisplayName = shortenDisplayName;

  itemAvatarPerson(item: MyIndicatorSummary): AvatarPerson {
    return { key: this.itemPatientKey(item), ...personAvatarPartsFromName(item.patientName) };
  }

  get filteredItems(): MyIndicatorSummary[] {
    if (this.patientFilterKey === ALL_PATIENTS_KEY) return this.items;
    return this.items.filter((i) => this.itemPatientKey(i) === this.patientFilterKey);
  }

  setPatientFilter(key: string): void {
    this.patientFilterKey = key;
  }

  specimenLabel(item: { specimenDisplayName: string | null }): string {
    return specimenLabel(item.specimenDisplayName);
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
      this.history = await this.api.getIndicatorHistory(
        item.analyteKey, item.specimenKbId, item.familyDependentId, item.targetUserId,
      );
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
