import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { ApiError, ApiService } from '../../services/api.service';
import { ToastService } from '../../shared/toast/toast.service';
import { HealthNoteCatalog, HealthNoteKind } from '../../models/types';
import { HealthNoteFormComponent } from '../health-note-form/health-note-form.component';
import type { VaccinationSavedInfo } from '../../services/vaccination-state.service';
import { todayLocal } from '../../shared/util/intake-labels';
import { formatDayMonthYear } from '../../shared/util/date-format';

type QuickKind = 'symptom' | 'temperature';

/**
 * Шторка «Прививка сохранена» (макет «Screen - Vaccination»): подсказка, когда ждать реакцию, и
 * (только для своих прививок — дневник строго личный, решение сентября 2026) быстрые записи
 * температуры/симптома + переключатель «напомнить о самочувствии через 7 дней». Для подопечных —
 * только подсказка, без дневника и без переключателя.
 */
@Component({
  selector: 'app-vaccination-saved-sheet',
  imports: [HealthNoteFormComponent],
  templateUrl: './vaccination-saved-sheet.component.html',
  styleUrl: './vaccination-saved-sheet.component.scss',
})
export class VaccinationSavedSheetComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  readonly info = input.required<VaccinationSavedInfo>();
  readonly closed = output<void>();

  protected readonly Kind = HealthNoteKind;
  readonly quickKind = signal<QuickKind | null>(null);
  readonly catalog = signal<HealthNoteCatalog | null>(null);
  // Все места создания прививки шлют requestWellbeingCheck: false — значит, напоминание выключено.
  // Раньше переключатель стартовал включённым, и человек ждал напоминания, которое не придёт.
  readonly reminderOn = signal(false);
  readonly reminderBusy = signal(false);

  /** «сегодня» — только если прививка и правда сегодняшняя; иначе реальная дата (раньше всегда
   * писалось «сегодня», даже для прошлой даты — выглядело, будто дата не сохранилась). */
  protected dateText(): string {
    const date = this.info().item.date;
    if (!date || date === todayLocal()) return 'сегодня';
    return formatDayMonthYear(date);
  }

  ngOnInit(): void {
    if (this.info().subject.isSelf) void this.loadCatalog();
  }

  protected openQuick(kind: QuickKind): void {
    this.quickKind.set(kind);
  }

  protected onQuickSaved(): void {
    this.quickKind.set(null);
    this.toast.success('Записано в дневник.');
  }

  protected async toggleReminder(checked: boolean): Promise<void> {
    const recordId = this.info().item.recordId;
    if (!recordId) return;
    this.reminderOn.set(checked);
    this.reminderBusy.set(true);
    try {
      await this.api.setVaccinationWellbeingCheck(recordId, checked);
    } catch (e) {
      this.reminderOn.set(!checked);
      this.toast.error(e instanceof ApiError ? e.message : 'Не удалось изменить напоминание.');
    } finally {
      this.reminderBusy.set(false);
    }
  }

  private async loadCatalog(): Promise<void> {
    try {
      this.catalog.set(await this.api.getHealthNoteCatalog());
    } catch {
      // Быстрые формы работают и без каталога (единицы измерения возьмутся из дефолта поля).
    }
  }
}
