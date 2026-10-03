import type { ActivatedRoute, Router } from '@angular/router';
import type {
  IndicatorDto, IndicatorHistoryPoint, KbAnalyteCard, PatientContextDto, UpdateIndicatorRequest,
} from '../../models/types';
import { ApiError, type ApiService } from '../../services/api.service';
import type { ConfirmService } from '../../shared/confirm/confirm.service';
import type { ToastService } from '../../shared/toast/toast.service';
import type { IndicatorInfoReading } from '../indicator-info/indicator-info.component';
import { indicatorLabel } from './indicator-display';
import { emptyIndicatorForm, sanitizeIndicatorForm } from './indicator-form';

export interface IndicatorInfoDeps {
  api: ApiService;
  router: Router;
  route: ActivatedRoute;
  toast: ToastService;
  confirm: ConfirmService;
  isWide(): boolean;
  /** Открытая запись (экран одной записи) или null в списке. */
  recordId(): string | null;
  indicatorsOf(recordId: string): readonly IndicatorDto[];
  /** Показатели записи перечитаны после правки/удаления — родитель сохраняет их и обновляет запись. */
  onIndicatorsChanged(recordId: string, indicators: IndicatorDto[]): void;
}

/**
 * Справка по показателю и его ручная правка (вынесено из MedicalRecordsPanelComponent).
 *
 * Два пути открытия делят одно состояние: клик по показателю записи (персонально — значение,
 * история, контекст пациента) и чип «что смотрят вместе» внутри уже открытой статьи (только
 * карточка справочника). На узком экране показатель открывается своим URL (?indicator=), чтобы
 * системное «назад» закрывало именно его; на широком панель справки — чисто in-memory.
 */
export class IndicatorInfoController {
  open = false;
  loading = false;
  error: string | null = null;
  card: KbAnalyteCard | null = null;
  displayName = '';
  reading: IndicatorInfoReading | null = null;
  history: IndicatorHistoryPoint[] | null = null;
  /** Возраст/пол пациента на дату записи (response.patient статьи). */
  patient: PatientContextDto | null = null;
  /** Показатель записи, чья статья открыта; null — открыта чипом «что смотрят вместе». */
  indicator: IndicatorDto | null = null;
  get indicatorId(): string | null {
    return this.indicator?.id ?? null;
  }

  // Правка показателя (ошибка OCR) — открывается из панели справки.
  editingId: string | null = null;
  editForm: UpdateIndicatorRequest = emptyIndicatorForm();
  saving = false;

  constructor(private readonly deps: IndicatorInfoDeps) {}

  /** navigate=false — вызов из подписки на маршрут или переоткрытие после правки: URL уже верный. */
  async openIndicator(indicator: IndicatorDto, navigate = true): Promise<void> {
    this.open = true;
    this.loading = true;
    this.error = null;
    this.card = null;
    this.history = null;
    this.patient = null;
    this.indicator = indicator;
    this.displayName = indicatorLabel(indicator);
    if (navigate && !this.deps.isWide()) {
      void this.deps.router.navigate([], {
        relativeTo: this.deps.route, queryParams: { indicator: indicator.id }, queryParamsHandling: 'merge',
      });
    }
    this.reading = {
      valueRaw: indicator.valueRaw,
      valueNumeric: indicator.valueNumericText !== null ? parseFloat(indicator.valueNumericText) : null,
      unit: indicator.unit,
      flag: indicator.flag,
      matchedRefRangeIndex: null,
    };
    try {
      const response = await this.deps.api.getIndicatorArticle(indicator.id);
      this.card = response.article;
      this.patient = response.patient;
      this.reading = { ...this.reading, matchedRefRangeIndex: response.matchedRefRangeIndex };
      if (response.historyAvailable) {
        this.history = await this.deps.api.getRecordIndicatorHistory(indicator.medicalRecordId, indicator.id);
      }
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить справку по показателю.';
    } finally {
      this.loading = false;
    }
  }

  /** Чип «что смотрят вместе» — другой показатель, без персонального контекста. */
  async openRelated(kbAnalyteId: string): Promise<void> {
    this.open = true;
    this.loading = true;
    this.error = null;
    this.card = null;
    this.reading = null;
    this.history = null;
    this.patient = null;
    this.displayName = '';
    this.indicator = null;
    try {
      this.card = await this.deps.api.getKbAnalyte(kbAnalyteId);
    } catch (err) {
      this.error = err instanceof ApiError ? err.message : 'Не удалось загрузить статью справочника.';
    } finally {
      this.loading = false;
    }
  }

  /** navigate=false — URL уже без ?indicator= или следом всё равно уходим на другой адрес. */
  close(navigate = true): void {
    this.open = false;
    this.indicator = null;
    this.cancelEdit();
    if (navigate && !this.deps.isWide()) {
      void this.deps.router.navigate([], {
        relativeTo: this.deps.route, queryParams: { indicator: null }, queryParamsHandling: 'merge',
      });
    }
  }

  /** Синхронизация с ?indicator= (полноэкранный показатель на узком экране). Вызывается из
   * подписки на queryParamMap и после загрузки показателей записи. */
  syncFromRoute(): void {
    if (this.deps.isWide()) return;
    const recordId = this.deps.recordId();
    if (!recordId) return;
    const id = this.deps.route.snapshot.queryParamMap.get('indicator');
    if (id) {
      if (this.indicatorId === id) return;
      const found = this.deps.indicatorsOf(recordId).find((i) => i.id === id);
      if (found) void this.openIndicator(found, false);
    } else if (this.open) {
      this.close(false);
    }
  }

  /** «Открыть в справочнике» — мини-хаб /health/kb/indicators сам откроет статью по ?id=. */
  openInCatalog(): void {
    if (!this.card) return;
    const id = this.card.id;
    this.close(false);
    void this.deps.router.navigate(['/health/kb/indicators'], { queryParams: { id } });
  }

  startEdit(indicator: IndicatorDto): void {
    this.editingId = indicator.id;
    this.editForm = {
      displayName: indicator.displayName,
      valueRaw: indicator.valueRaw,
      unit: indicator.unit,
      refLowText: indicator.refLowText,
      refHighText: indicator.refHighText,
      refText: indicator.refText,
    };
  }

  cancelEdit(): void {
    this.editingId = null;
    this.editForm = emptyIndicatorForm();
  }

  async saveEdit(recordId: string): Promise<void> {
    if (!this.editingId) return;
    if (!this.editForm.displayName.trim()) {
      this.deps.toast.error('Укажите название показателя.');
      return;
    }
    const savedId = this.editingId;
    this.saving = true;
    try {
      await this.deps.api.updateIndicator(savedId, sanitizeIndicatorForm(this.editForm));
      const indicators = await this.deps.api.getRecordIndicators(recordId);
      this.cancelEdit();
      this.deps.onIndicatorsChanged(recordId, indicators);
      // Правили открытый показатель — панель сразу показывает новое значение/статус/шкалу.
      const updated = indicators.find((i) => i.id === savedId);
      if (updated && this.indicatorId === savedId) void this.openIndicator(updated, false);
    } catch (err) {
      this.deps.toast.error(err instanceof ApiError ? err.message : 'Не удалось сохранить правку — возможно, такой показатель уже есть в записи.');
    } finally {
      this.saving = false;
    }
  }

  async delete(recordId: string, indicator: IndicatorDto): Promise<void> {
    const confirmed = await this.deps.confirm.confirm({
      title: 'Удалить показатель?',
      message: `«${indicator.displayName}» будет удалён из записи безвозвратно.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!confirmed) return;

    try {
      await this.deps.api.deleteIndicator(indicator.id);
      const indicators = await this.deps.api.getRecordIndicators(recordId);
      if (this.indicatorId === indicator.id) this.close();
      this.deps.onIndicatorsChanged(recordId, indicators);
    } catch (err) {
      this.deps.toast.error(err instanceof ApiError ? err.message : 'Не удалось удалить показатель.');
    }
  }
}
